using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Hr.Application;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// The daily run of a tenant (spec 2026-11-23 §9.1). From the local hour <c>Hr:SendHourLocal</c>, only if the tenant is <c>Active</c>
/// and has the module <c>hr</c>, once per tenant and day (<see cref="TenantRunStore"/>) and one digest per manager and day
/// (<see cref="DailyDigestStore"/>). The count is <c>ExitPending + NoExams + Overdue + DueSoon</c>, taken from the same read as the
/// control screen; with zero nothing is published. The managers are whoever has <c>hr.medical.manage</c>. An error with one manager is
/// logged and does not stop the others.
/// </summary>
internal sealed partial class HrMedicalExamsReminderProcessor(
    HrDbContext dbContext,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    IMedicalExamOverviewReader overview,
    IHrMemberDirectory members,
    TenantRunStore runs,
    DailyDigestStore digests,
    MedicalExamEventPublisher events,
    IOptions<HrOptions> options,
    ILogger<HrMedicalExamsReminderProcessor> logger) : IHrMedicalExamsReminderProcessor
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Medical exams digest failed for member {MemberId} of tenant {TenantId}.")]
    private static partial void LogDigestFailed(ILogger logger, Exception exception, Guid memberId, Guid tenantId);

    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        if (calendar.ToLocal(calendar.UtcNow).Hour < options.Value.SendHourLocal)
        {
            return;
        }

        // A suspended tenant (any state but Active) gets no service. null is the stub tenant, without a row.
        if (await tenantDirectory.GetStatusAsync(new TenantId(tenantId), cancellationToken) != TenantStatus.Active)
        {
            return;
        }

        // The module may be turned off with employees already created: without it nothing is announced. null is the stub tenant.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Hr))
        {
            return;
        }

        var today = calendar.Today;
        var now = calendar.UtcNow;
        if (!await runs.TryClaimAsync(tenantId, today, now, cancellationToken))
        {
            return;
        }

        var pending = await PendingCountAsync(tenantId, today, cancellationToken);
        if (pending > 0)
        {
            await SendDigestsAsync(tenantId, today, now, pending, cancellationToken);
        }

        // A Sending that nobody retook can no longer be retaken: it is closed as send_failed instead of hanging.
        await digests.FailStaleAsync(tenantId, today, now, cancellationToken);
        var finishedAt = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        await runs.TryFinishAsync(tenantId, today, finishedAt, cancellationToken);
    }

    private async Task<int> PendingCountAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var page = await overview.ReadAsync(tenantId, [], null, today, 1, 1, cancellationToken);
        return new[] { MedicalExamStatus.ExitPending, MedicalExamStatus.NoExams, MedicalExamStatus.Overdue, MedicalExamStatus.DueSoon }
            .Sum(status => page.Counts.GetValueOrDefault(status));
    }

    private async Task SendDigestsAsync(Guid tenantId, DateOnly today, DateTimeOffset now, int pending, CancellationToken cancellationToken)
    {
        var managers = await members.ListWithPermissionAsync(tenantId, HrPermissions.MedicalManage, cancellationToken);
        foreach (var manager in managers)
        {
            try
            {
                if (!await digests.TryClaimAsync(tenantId, manager.MemberId, today, now, cancellationToken))
                {
                    continue;
                }

                try
                {
                    await events.PublishDueDigestAsync(tenantId, manager.MemberId, today, pending, now, cancellationToken);
                    await digests.MarkSentAsync(tenantId, manager.MemberId, today, now, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await digests.MarkFailedAsync(tenantId, manager.MemberId, today, "publish_failed", cancellationToken);
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDigestFailed(logger, exception, manager.MemberId, tenantId);
            }
            finally
            {
                // What did not save does not come back in the SaveChanges of the next manager.
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
