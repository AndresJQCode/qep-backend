using BuildingBlocks.Application;
using BuildingBlocks.Application.BusinessHours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Spec 2026-11-22 §7 and D-15/D-16: every <see cref="SupportWorkerOptions.UnassignedAlertIntervalMinutes"/> (5) it looks, tenant by tenant
/// (only those with the alert on), for tickets that have been without an assignee for the tenant's threshold of <b>business</b> hours, and
/// publishes <c>support.ticket.unassigned-alert.v1</c> to the recipients the tenant chose. The hours are counted by
/// <see cref="IBusinessHoursProvider"/> from <c>max(unassigned_since, unassigned_alert_enabled_at)</c> to now, so turning the alert on does
/// not fire for old tickets, and a ticket that waited on the customer starts again when it comes back (the clock is the domain's).
/// <para>Before looking, it heals the tickets written by a pod from before this change (unassigned, in a status that counts, with a null
/// <c>unassigned_since</c>), with an <c>UPDATE</c> that does not touch <c>version</c>.</para>
/// <para>One alert per ticket and per stretch, however many replicas run it: the claim is an <c>INSERT … ON CONFLICT DO NOTHING</c> on
/// <c>(ticket_id, unassigned_since)</c> in the same transaction as the event, so the loser of a race inserts nothing and publishes nothing.
/// The ticket row is never written, so an alert can never give a 412 to whoever is editing the ticket. One tenant's or one ticket's
/// failure is logged and the rest go on.</para>
/// </summary>
internal sealed partial class SupportUnassignedAlertWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SupportWorkerOptions> options,
    ILogger<SupportUnassignedAlertWorker> logger) : BackgroundService
{
    /// <summary>Tickets looked at per tenant per tick. Oldest first, and the worker stops at the first one under the threshold, so what is
    /// left is picked up on the next tick.</summary>
    internal const int CandidatesPerTenant = 200;

    [LoggerMessage(Level = LogLevel.Error, Message = "Support unassigned-alert tick failed; it runs again on the next interval.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The unassigned alert of tenant {TenantId} failed with {ExceptionType}; it is tried again on the next tick.")]
    private static partial void LogTenantFailed(ILogger logger, Guid tenantId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The unassigned alert of ticket {TicketId} failed with {ExceptionType}; it is tried again on the next tick.")]
    private static partial void LogTicketFailed(ILogger logger, Guid ticketId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} of tenant {TenantId} is past the unassigned threshold and has no recipient able to receive the alert.")]
    private static partial void LogNobodyToTell(ILogger logger, Guid ticketId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Healed {Count} unassigned tickets of tenant {TenantId} that had no unassigned_since.")]
    private static partial void LogHealed(ILogger logger, int count, Guid tenantId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.UnassignedAlertIntervalMinutes)));
        do
        {
            try
            {
                // IClock is scoped in this application: it is read from a scope of the tick, as the retention worker of Notifications does.
                DateTimeOffset now;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                }

                await RunOnceAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every tenant with the alert on. Returns how many alerts it claimed (those with nobody to tell included).</summary>
    internal async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Npgsql only writes UTC instants, and everything below is compared as instants.
        now = now.ToUniversalTime();
        IReadOnlyList<SupportTenantSettings> tenants;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<SupportUnassignedAlertStore>().ListEnabledAsync(cancellationToken);
        }

        var claimed = 0;
        foreach (var settings in tenants)
        {
            try
            {
                claimed += await RunTenantAsync(settings, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTenantFailed(logger, settings.TenantId, exception.GetType().Name);
            }
        }

        return claimed;
    }

    private async Task<int> RunTenantAsync(SupportTenantSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = settings.TenantId;
        IReadOnlyList<Guid> candidates;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<SupportUnassignedAlertStore>();
            var healed = await store.HealAsync(tenantId, now, cancellationToken);
            if (healed > 0)
            {
                LogHealed(logger, healed, tenantId);
            }

            // Business hours never exceed wall-clock hours: a ticket younger than the threshold on the wall clock cannot have reached it.
            candidates = await store.ListCandidateIdsAsync(
                tenantId, now.AddHours(-settings.UnassignedAlertHours), CandidatesPerTenant, cancellationToken);
        }

        var claimed = 0;
        IReadOnlyList<Guid>? recipients = null;
        // One scope for the whole tenant pass: the provider resolves the schedule and the time zone once and reuses them for every candidate
        // (it memoizes per instance), instead of three or four queries per ticket. The claim and the event still use a scope per ticket.
        await using var tenantScope = scopeFactory.CreateAsyncScope();
        var hours = tenantScope.ServiceProvider.GetRequiredService<IBusinessHoursProvider>();
        foreach (var ticketId in candidates)
        {
            try
            {
                var outcome = await AlertAsync(settings, ticketId, now, hours, recipients, cancellationToken);
                recipients = outcome.Recipients;
                if (outcome.UnderThreshold)
                {
                    // Oldest first: if this one has not reached the threshold in business hours, no later one has.
                    break;
                }

                if (outcome.Claimed)
                {
                    claimed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTicketFailed(logger, ticketId, exception.GetType().Name);
            }
        }

        return claimed;
    }

    // One ticket in its own scope and transaction: the claim and the event commit together or not at all.
    private async Task<AlertOutcome> AlertAsync(
        SupportTenantSettings settings,
        Guid ticketId,
        DateTimeOffset now,
        IBusinessHoursProvider hours,
        IReadOnlyList<Guid>? knownRecipients,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<SupportUnassignedAlertStore>();
        var ticket = await store.FindTicketAsync(settings.TenantId, ticketId, cancellationToken);
        if (ticket?.UnassignedSince is not { } since)
        {
            // Assigned, moved or closed between the list and the read: nothing to tell.
            return new AlertOutcome(false, false, knownRecipients);
        }

        // The floor of the clock: turning the alert on does not fire for tickets that were already waiting.
        var from = settings.UnassignedAlertEnabledAt is { } enabledAt && enabledAt > since ? enabledAt : since;
        var elapsed = await hours.ElapsedBusinessSecondsAsync(
            settings.TenantId, settings.UnassignedAlertScheduleId, from, now, cancellationToken);
        if (elapsed.BusinessSeconds < settings.UnassignedAlertHours * 3600L)
        {
            return new AlertOutcome(false, true, knownRecipients);
        }

        // The recipients who can receive today, in the order the tenant chose; read once per pass and only if somebody crossed.
        var recipients = knownRecipients ?? await ReceivingRecipientsAsync(services, settings, cancellationToken);
        var unitOfWork = services.GetRequiredService<ISupportUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var won = await store.TryClaimAsync(settings.TenantId, ticketId, since, recipients.Count, now, cancellationToken);
        if (!won)
        {
            // Another replica (or an earlier tick) already claimed this stretch: nothing is published twice.
            return new AlertOutcome(false, false, recipients);
        }

        if (recipients.Count > 0)
        {
            services.GetRequiredService<ISupportIntegrationEvents>()
                .PublishUnassignedAlert(ticket, recipients, settings.UnassignedAlertHours, since, now);
        }
        else
        {
            LogNobodyToTell(logger, ticketId, settings.TenantId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AlertOutcome(true, false, recipients);
    }

    private static async Task<IReadOnlyList<Guid>> ReceivingRecipientsAsync(
        IServiceProvider services, SupportTenantSettings settings, CancellationToken cancellationToken)
    {
        if (settings.UnassignedAlertRecipientIds.Length == 0)
        {
            return [];
        }

        var found = (await services.GetRequiredService<ISupportMemberDirectory>()
            .FindAsync(settings.TenantId, settings.UnassignedAlertRecipientIds, cancellationToken)).ToDictionary(member => member.MemberId);
        return settings.UnassignedAlertRecipientIds
            .Where(id => found.TryGetValue(id, out var member) && member.IsActive && member.CanManage)
            .ToArray();
    }

    private readonly record struct AlertOutcome(bool Claimed, bool UnderThreshold, IReadOnlyList<Guid>? Recipients);
}
