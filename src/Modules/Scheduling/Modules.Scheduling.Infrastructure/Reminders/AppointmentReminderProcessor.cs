using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Scheduling.Infrastructure.Reminders;

/// <summary>
/// Plan 2026-11-21, Tarea 12 y spec «Recordatorio por WhatsApp» (D-9): una pasada del barredor. Toma las citas <c>Confirmed</c> que aún no
/// empezaron, sin recordatorio enviado ni omitido, cuyo aviso ya cayó en la ventana (<c>starts_at - reminder_offset_hours &lt;= ahora</c>) y
/// de tenants con la Agenda <b>y</b> Mensajería prendidas y el recordatorio activo. Cada cita se <b>reclama</b> con un <c>UPDATE</c> (sube
/// <c>reminder_attempts</c> y fija <c>reminder_claimed_until</c>): con varios pods gana uno, y un fallo vuelve a salir sólo cuando vence el
/// reclamo, hasta <see cref="AppointmentReminderRules.MaxAttempts"/>. El envío lleva la llave <c>appointment-reminder:{id}</c>: si salió y
/// marcar la cita falló, el reintento devuelve el mismo mensaje en vez de mandar otro. Lo que no puede salir (sin teléfono, consentimiento
/// revocado, <c>MarketingOptOut</c>, sin miembro que firme, fallos agotados) queda con su motivo en la cita y en el historial, y no se
/// vuelve a evaluar.
/// </summary>
internal sealed partial class AppointmentReminderProcessor(IServiceScopeFactory scopeFactory, ILogger<AppointmentReminderProcessor> logger)
{
    /// <summary>The longest code kept in <c>reminder_last_error</c>.</summary>
    internal const int LastErrorMaxLength = 100;

    // One array for the claim parameter (CA1861: not built on every call).
    private static readonly TimeSpan[] LeaseArray = [.. AppointmentReminderRules.Leases];

    [LoggerMessage(Level = LogLevel.Error, Message = "The appointment reminder {AppointmentId} of tenant {TenantId} failed unexpectedly on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid appointmentId, Guid tenantId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The appointment {AppointmentId} of tenant {TenantId} changed while its reminder was being recorded; it is evaluated again on the next pass.")]
    private static partial void LogConcurrentChange(ILogger logger, Guid appointmentId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The appointment reminders of tenant {TenantId} could not be checked.")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    /// <summary>Una pasada. Devuelve cuántas citas reclamó y trató (enviadas, omitidas o con fallo), no cuántas salieron.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var due = await ListDueAsync(cancellationToken);
        var handled = 0;
        foreach (var tenantRows in due.GroupBy(row => row.TenantId))
        {
            try
            {
                foreach (var row in tenantRows)
                {
                    if (await ProcessAsync(row, cancellationToken))
                    {
                        handled++;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A tenant that fails does not stop the others.
                LogTenantFailed(logger, exception, tenantRows.Key);
            }
        }

        return handled;
    }

    // The batch is ordered by start across every tenant, and a tenant without Scheduling or Messaging is never claimed: its rows would come
    // back first on every pass and, with a batch of their size, starve everyone behind them. So the tenants found disabled are left out of
    // the query and the batch is read again until it holds only rows of tenants that may be reminded (each round excludes at least one
    // more tenant, so it ends). Turning the module on later still finds those appointments untouched.
    private async Task<List<DueRow>> ListDueAsync(CancellationToken cancellationToken)
    {
        var skipped = new List<Guid>();
        var allowed = new Dictionary<Guid, bool>();
        while (true)
        {
            var rows = await QueryDueAsync([.. skipped], cancellationToken);
            var disabled = new List<Guid>();
            foreach (var tenantId in rows.Select(row => row.TenantId).Distinct())
            {
                if (!allowed.TryGetValue(tenantId, out var isAllowed))
                {
                    isAllowed = await ModulesAllowAsync(tenantId, cancellationToken);
                    allowed[tenantId] = isAllowed;
                }

                if (!isAllowed)
                {
                    disabled.Add(tenantId);
                }
            }

            if (disabled.Count == 0)
            {
                return rows;
            }

            skipped.AddRange(disabled);
        }
    }

    private async Task<List<DueRow>> QueryDueAsync(Guid[] skippedTenants, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.ToUniversalTime();
        // The sender uses the approved template of the connection (plan, correction 47; spec 2026-11-23 §2).
        return await dbContext.Database.SqlQuery<DueRow>(
            $"""
            SELECT a.id AS "AppointmentId", a.tenant_id AS "TenantId"
              FROM scheduling.appointments a
              JOIN scheduling.settings s ON s.tenant_id = a.tenant_id
             WHERE s.reminder_enabled AND s.reminder_connection_id IS NOT NULL
               AND a.tenant_id <> ALL({skippedTenants})
               AND a.status = 'Confirmed' AND a.reminder_sent_at IS NULL AND a.reminder_skipped_reason IS NULL
               AND a.starts_at > {now}
               AND a.starts_at - make_interval(hours => s.reminder_offset_hours) <= {now}
               AND a.reminder_attempts < {AppointmentReminderRules.MaxAttempts}
               AND (a.reminder_claimed_until IS NULL OR a.reminder_claimed_until < {now})
             ORDER BY a.starts_at, a.id
             LIMIT {AppointmentReminderRules.BatchSize}
            """).ToListAsync(cancellationToken);
    }

    // Without Messaging there is no template to send and nothing to claim. A tenant whose modules cannot be read is left out of this pass.
    private async Task<bool> ModulesAllowAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var modules = await scope.ServiceProvider.GetRequiredService<ITenantModules>().FindAsync(tenantId, cancellationToken);
            return modules is not null
                && modules.IsEnabled(TenantModuleKeys.Scheduling)
                && modules.IsEnabled(TenantModuleKeys.Messaging);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, tenantId);
            return false;
        }
    }

    private async Task<bool> ProcessAsync(DueRow row, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<SchedulingDbContext>();
        var now = services.GetRequiredService<IClock>().UtcNow.ToUniversalTime();

        // The claim is its own statement and commits by itself: from here on only this pod handles the appointment until the lease ends.
        var attempt = await ClaimAsync(dbContext, row, now, cancellationToken);
        if (attempt is null)
        {
            return false;
        }

        var state = new PassState();
        try
        {
            await HandleAsync(services, dbContext, row, attempt.Value, now, state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The claim stays alive: with attempts left it comes back when the lease ends. On the last attempt there is no next time, so
            // the pass settles the appointment now (never leaves it with the attempts spent and neither a sent mark nor a reason).
            if (exception is DbUpdateConcurrencyException)
            {
                LogConcurrentChange(logger, row.AppointmentId, row.TenantId);
            }
            else
            {
                LogAttemptFailed(logger, exception, row.AppointmentId, row.TenantId, attempt.Value);
            }

            if (attempt.Value >= AppointmentReminderRules.MaxAttempts)
            {
                await SettleLastAttemptAsync(row, attempt.Value, state, now, cancellationToken);
            }
        }

        return true;
    }

    // The last attempt failed after the claim: the context that failed may be broken, so the whole evaluation runs once more in a fresh
    // scope, which reads the appointment as it is now (cancelled, already recorded, changed by someone) and sends again with the same
    // idempotency key, which Messaging answers with the message it already has. If even that fails: a message that did go out is marked
    // as sent with a plain statement, and one that did not is given up with a reason.
    private async Task SettleLastAttemptAsync(
        DueRow row, int attempt, PassState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
            await HandleAsync(scope.ServiceProvider, dbContext, row, attempt, now, state, cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAttemptFailed(logger, exception, row.AppointmentId, row.TenantId, attempt);
        }

        if (state.Delivered)
        {
            await MarkSentAsync(row, now, cancellationToken);
        }
        else
        {
            await GiveUpAsync(row, SendFailed("unexpected_error"), now, cancellationToken);
        }
    }

    // Last resort for a reminder that did go out: only the column, no history line (the entity could not be saved), so the sweeper stops
    // treating the appointment as pending.
    private async Task MarkSentAsync(DueRow row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE scheduling.appointments SET reminder_sent_at = {now}, reminder_last_error = NULL
                 WHERE id = {row.AppointmentId} AND tenant_id = {row.TenantId}
                   AND reminder_sent_at IS NULL AND reminder_skipped_reason IS NULL
                """,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, row.TenantId);
        }
    }

    private static async Task<int?> ClaimAsync(SchedulingDbContext dbContext, DueRow row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = LeaseArray;
        var claimed = await dbContext.Database.SqlQuery<int>(
            $"""
            UPDATE scheduling.appointments
               SET reminder_claimed_until = {now} + ({leases})[LEAST(reminder_attempts + 1, cardinality({leases}))],
                   reminder_attempts = reminder_attempts + 1
             WHERE id = {row.AppointmentId} AND tenant_id = {row.TenantId}
               AND status = 'Confirmed' AND reminder_sent_at IS NULL AND reminder_skipped_reason IS NULL
               AND starts_at > {now}
               AND reminder_attempts < {AppointmentReminderRules.MaxAttempts}
               AND (reminder_claimed_until IS NULL OR reminder_claimed_until < {now})
            RETURNING reminder_attempts AS "Value"
            """).ToListAsync(cancellationToken);
        return claimed.Count == 1 ? claimed[0] : null;
    }

    private static async Task HandleAsync(
        IServiceProvider services,
        SchedulingDbContext dbContext,
        DueRow row,
        int attempt,
        DateTimeOffset now,
        PassState state,
        CancellationToken cancellationToken)
    {
        // Read after the claim: the attempts and the history are the ones this pass owns.
        var appointmentId = new AppointmentId(row.AppointmentId);
        var appointment = await dbContext.Appointments
            .Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.TenantId == row.TenantId && value.Id == appointmentId, cancellationToken);
        var settings = await dbContext.Settings.AsNoTracking().SingleOrDefaultAsync(value => value.TenantId == row.TenantId, cancellationToken);

        // The appointment changed between the selection and the claim (cancelled, moved to the past) or the tenant switched the reminder off.
        if (appointment is not { Status: AppointmentStatus.Confirmed, ReminderSentAt: null, ReminderSkippedReason: null }
            || appointment.StartsAt <= now
            || settings is not { ReminderEnabled: true, ReminderConnectionId: { } connectionId })
        {
            return;
        }

        var recipient = await services.GetRequiredService<ISchedulingReminderRecipients>()
            .FindAsync(row.TenantId, appointment.CustomerId, cancellationToken);
        if (SkipReasonOf(recipient) is { } reason)
        {
            await SkipAsync(dbContext, appointment, reason, now, cancellationToken);
            return;
        }

        var resource = await dbContext.Resources.AsNoTracking()
            .Where(value => value.Id == appointment.ResourceId)
            .Select(value => new { value.DisplayName, value.MemberId })
            .SingleAsync(cancellationToken);
        var serviceName = await dbContext.Services.AsNoTracking()
            .Where(value => value.Id == appointment.ServiceId).Select(value => value.Name).SingleAsync(cancellationToken);
        var locationName = await dbContext.Locations.AsNoTracking()
            .Where(value => value.Id == appointment.LocationId).Select(value => value.Name).SingleAsync(cancellationToken);

        if (await SenderOfAsync(services, appointment, resource.MemberId, settings.ReminderSenderMemberId, cancellationToken) is not { } senderMemberId)
        {
            await SkipAsync(dbContext, appointment, ReminderSkipReasons.NoSenderMember, now, cancellationToken);
            return;
        }

        var message = new ReminderMessage(
            row.TenantId, row.AppointmentId, AppointmentReminderRules.KeyOf(row.AppointmentId, appointment.StartsAt), connectionId, senderMemberId,
            appointment.CustomerId, recipient!.Name, recipient.PhoneE164!, serviceName, resource.DisplayName, locationName, appointment.StartsAt);
        var delivery = await services.GetRequiredService<IAppointmentReminderSender>().SendAsync(message, cancellationToken);

        switch (delivery)
        {
            case ReminderDelivery.Sent:
                // From here on the client has the message: whatever fails next must not be recorded as a failed send.
                state.Delivered = true;
                appointment.RecordReminderSent(now);
                await dbContext.SaveChangesAsync(cancellationToken);
                break;
            case ReminderDelivery.Failed failed:
                // The code is written by its own statement: the appointment row is only touched (version and history) when it gives up.
                await dbContext.Database.ExecuteSqlAsync(
                    $"UPDATE scheduling.appointments SET reminder_last_error = {Truncate(failed.Code, LastErrorMaxLength)} WHERE id = {row.AppointmentId}",
                    cancellationToken);
                if (attempt >= AppointmentReminderRules.MaxAttempts)
                {
                    await SkipAsync(dbContext, appointment, SendFailed(failed.Code), now, cancellationToken);
                }

                break;
            default:
                throw new InvalidOperationException($"Unexpected reminder delivery {delivery.GetType().Name}.");
        }
    }

    // The order matters for the reason a person reads: a revoked consent is said before a missing phone.
    private static string? SkipReasonOf(ReminderRecipient? recipient) => recipient switch
    {
        null => ReminderSkipReasons.CustomerMissing,
        { IsActive: false } => ReminderSkipReasons.CustomerInactive,
        { ConsentRevoked: true } => ReminderSkipReasons.ConsentRevoked,
        { MarketingOptOut: true } => ReminderSkipReasons.MarketingOptOut,
        _ when !AppointmentReminderRules.IsDialable(recipient.PhoneE164) => ReminderSkipReasons.NoPhone,
        _ => null,
    };

    // Messaging needs an author for the message: the member linked to the resource and, without one (or inactive), whoever touched the
    // appointment last and, last of all, the default sender of the rules (D-18: an appointment the AI agent made has no human actor).
    // Neither one active is a reason to skip, not a guess.
    private static async Task<Guid?> SenderOfAsync(
        IServiceProvider services, Appointment appointment, Guid? resourceMemberId, Guid? defaultSenderMemberId, CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        if (resourceMemberId is { } linked)
        {
            candidates.Add(linked);
        }

        candidates.AddRange(appointment.Events
            .Where(entry => entry.ActorMemberId is not null)
            .OrderByDescending(entry => entry.At)
            .Select(entry => entry.ActorMemberId!.Value));
        if (defaultSenderMemberId is { } fallback)
        {
            candidates.Add(fallback);
        }

        candidates = [.. candidates.Distinct()];
        if (candidates.Count == 0)
        {
            return null;
        }

        var members = await services.GetRequiredService<ISchedulingMemberDirectory>().FindAsync(appointment.TenantId, candidates, cancellationToken);
        var active = members.Where(member => member.IsActive).Select(member => member.MemberId).ToHashSet();
        return candidates.Where(active.Contains).Select(id => (Guid?)id).FirstOrDefault();
    }

    private static async Task SkipAsync(
        SchedulingDbContext dbContext, Appointment appointment, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        appointment.RecordReminderSkipped(reason, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // For an unexpected failure on the last attempt: a fresh scope, because the one that failed may have a context in a broken state.
    private async Task GiveUpAsync(DueRow row, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
            var appointmentId = new AppointmentId(row.AppointmentId);
            var appointment = await dbContext.Appointments
                .Include(value => value.Events)
                .SingleOrDefaultAsync(value => value.TenantId == row.TenantId && value.Id == appointmentId, cancellationToken);
            if (appointment is { Status: AppointmentStatus.Confirmed, ReminderSentAt: null, ReminderSkippedReason: null })
            {
                await SkipAsync(dbContext, appointment, reason, now, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, row.TenantId);
        }
    }

    private static string SendFailed(string code) =>
        Truncate(ReminderSkipReasons.SendFailedPrefix + code, Appointment.ReminderReasonMaxLength);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record DueRow(Guid AppointmentId, Guid TenantId);

    /// <summary>What one pass learned before it failed: whether Messaging already accepted the message.</summary>
    private sealed class PassState
    {
        public bool Delivered { get; set; }
    }
}
