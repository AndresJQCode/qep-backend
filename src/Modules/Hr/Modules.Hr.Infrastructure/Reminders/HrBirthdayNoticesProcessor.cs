using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Hr.Application;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// The daily birthday run of a tenant (spec 2026-11-26 §5.2), a sibling of <see cref="HrMedicalExamsReminderProcessor"/> with the same
/// gates: from <c>Hr:SendHourLocal</c>, tenant <c>Active</c>, module <c>hr</c> on. A tenant without a setting (or without managers)
/// returns before claiming anything. Once per tenant and day (<see cref="BirthdayRunStore"/>) and one digest per manager and day
/// (<see cref="BirthdayDigestStore"/>). The recipients are the managers of the setting that still read employees at run time; one that
/// lost access or is no longer active is skipped silently. An error with one manager is logged and does not stop the others.
/// </summary>
internal sealed partial class HrBirthdayNoticesProcessor(
    HrDbContext dbContext,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    IBirthdayNoticeSettingRepository settings,
    IHrMemberDirectory members,
    BirthdayRunStore runs,
    BirthdayDigestStore digests,
    BirthdayEventPublisher events,
    IOptions<HrOptions> options,
    ILogger<HrBirthdayNoticesProcessor> logger) : IHrBirthdayNoticesProcessor
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Birthday digest failed for member {MemberId} of tenant {TenantId}.")]
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

        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Hr))
        {
            return;
        }

        // No setting, or nobody to tell: nothing is claimed, so enabling it later the same day still runs.
        var setting = await settings.FindAsync(tenantId, cancellationToken);
        if (setting is null || setting.ManagerMemberIds.Count == 0)
        {
            return;
        }

        var today = calendar.Today;
        var now = calendar.UtcNow;
        if (!await runs.TryClaimAsync(tenantId, today, now, cancellationToken))
        {
            return;
        }

        var employees = await ActiveBirthdaysAsync(tenantId, cancellationToken);
        var digest = BirthdayDigest.Compose(employees, today, setting.LeadDays);
        if (digest.TodayTotal > 0 || digest.UpcomingTotal > 0)
        {
            await SendDigestsAsync(setting, calendar, digest, cancellationToken);
        }

        // A Sending that nobody retook can no longer be retaken: it is closed as send_failed instead of hanging.
        await digests.FailStaleAsync(tenantId, today, now, cancellationToken);
        var finishedAt = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        await runs.TryFinishAsync(tenantId, today, finishedAt, cancellationToken);
    }

    // Only the active employees with a birth date, projected to what the digest needs: nothing else of the employee is read.
    private async Task<IReadOnlyList<(Guid Id, string Name, DateOnly BirthDate)>> ActiveBirthdaysAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId && employee.Status == EmployeeStatus.Active && employee.BirthDate != null)
            .Select(employee => new { employee.Id, Name = employee.FirstName + " " + employee.LastName, employee.BirthDate })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.Id.Value, row.Name, row.BirthDate!.Value)).ToList();
    }

    private async Task SendDigestsAsync(
        BirthdayNoticeSetting setting,
        TenantCalendar calendar,
        (IReadOnlyList<BirthdayPerson> Today, int TodayTotal, IReadOnlyList<BirthdayPerson> Upcoming, int UpcomingTotal, DateOnly? UpcomingDate) digest,
        CancellationToken cancellationToken)
    {
        var tenantId = setting.TenantId;
        var today = calendar.Today;
        var now = calendar.UtcNow;
        var withAccess = (await members.ListWithPermissionAsync(tenantId, HrPermissions.EmployeeRead, cancellationToken))
            .Select(member => member.MemberId).ToHashSet();
        var channels = new BirthdayDigestChannels(
            setting.WhatsAppEnabled ? setting.WhatsAppConnectionId : null, setting.EmailEnabled, setting.UpdatedByMemberId);
        // Midnight of the next local day, as UTC: after it the notice is no longer news.
        var expiresAt = calendar.StartOfDayUtc(today.AddDays(1));

        // Materialized: the clear of the change tracker below must not touch the list being walked.
        foreach (var memberId in setting.ManagerMemberIds.Where(withAccess.Contains).ToList())
        {
            try
            {
                if (!await digests.TryClaimAsync(tenantId, memberId, today, now, cancellationToken))
                {
                    continue;
                }

                try
                {
                    var payload = new BirthdayDigestPayload(
                        tenantId, memberId, today, digest.Today, digest.TodayTotal, digest.Upcoming, digest.UpcomingTotal,
                        digest.UpcomingDate, channels, expiresAt);
                    await events.PublishDigestAsync(payload, now, cancellationToken);
                    await digests.MarkSentAsync(tenantId, memberId, today, now, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await digests.MarkFailedAsync(tenantId, memberId, today, "publish_failed", cancellationToken);
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDigestFailed(logger, exception, memberId, tenantId);
            }
            finally
            {
                // What did not save does not come back in the SaveChanges of the next manager.
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
