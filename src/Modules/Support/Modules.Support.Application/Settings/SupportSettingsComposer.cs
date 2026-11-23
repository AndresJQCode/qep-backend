using BuildingBlocks.Application.BusinessHours;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>
/// Spec 2026-11-22 §8.6: builds <see cref="SupportSettingsDto"/>, including the «ticket sin asignar» block. Everything the screen has to
/// show about the alert is resolved here (BFF): the recipients by name and whether each can receive today, the options to choose from, and
/// which schedule rules and why. The schedule options come from <see cref="IBusinessHoursProvider"/> and are served under Support's own
/// settings permission, without asking for <c>messaging.conversation.read</c>: schedule names are not sensitive, and whoever configures
/// Support has to be able to choose one.
/// </summary>
public sealed class SupportSettingsComposer(
    ISupportMemberDirectory members,
    IBusinessHoursProvider businessHours,
    ITenantClock tenantClock,
    ISupportTaskNoticeChannels taskNoticeChannels)
{
    internal const string ScheduleChosen = "Chosen";
    internal const string ScheduleDefault = "Default";
    internal const string ScheduleChosenMissing = "ChosenMissing";
    internal const string ScheduleNone = "None";

    public async Task<SupportSettingsDto> ComposeAsync(SupportTenantSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var alert = settings.UnassignedAlert;
        var tenantId = settings.TenantId;

        var schedules = await businessHours.ListSchedulesAsync(tenantId, cancellationToken);
        var managers = await members.ListManagersAsync(tenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);

        var recipients = await RecipientsAsync(tenantId, alert.RecipientIds, cancellationToken);
        var unassignedAlert = new SupportUnassignedAlertDto(
            alert.Enabled,
            alert.ThresholdHours,
            SupportUnassignedAlertSettings.MinThresholdHours,
            SupportUnassignedAlertSettings.MaxThresholdHours,
            alert.ScheduleId,
            ScheduleOf(alert.ScheduleId, schedules),
            schedules
                .Select(schedule => new SupportAlertScheduleOptionDto(schedule.Id, schedule.Name, schedule.IsDefault, schedule.HasWeeklyHours))
                .ToArray(),
            calendar.TimeZone.Id,
            SupportUnassignedAlertSettings.MaxRecipients,
            recipients,
            // The same single order of the assignees list: invariant and with the id as tiebreaker, so it does not depend on the server culture.
            managers
                .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(member => member.MemberId)
                .Select(member => new SupportAlertRecipientOptionDto(member.MemberId, member.DisplayName))
                .ToArray(),
            recipients.Count(recipient => recipient.CanReceive));

        // The stored connection is shown as it is, listed or not: one that was paused or lost its template is not erased by a read.
        var taskNotice = new SupportTaskNoticeDto(
            settings.TaskNoticeConnectionId,
            (await taskNoticeChannels.ListAsync(tenantId, cancellationToken))
                .OrderBy(connection => connection.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(connection => connection.ConnectionId)
                .Select(connection => new SupportTaskNoticeConnectionDto(
                    connection.ConnectionId, connection.Name, connection.PhoneNumber, connection.TemplateStatus))
                .ToArray(),
            taskNoticeChannels.EscalationAfterMinutes);

        return SupportDtoMapping.ToDto(settings, unassignedAlert, taskNotice);
    }

    // The schedule that rules today and why, the way IBusinessHoursProvider resolves it: the chosen one if it still exists, else the
    // tenant's default, else none (around the clock).
    private static SupportAlertScheduleDto ScheduleOf(Guid? scheduleId, IReadOnlyList<BusinessHoursScheduleRef> schedules)
    {
        var defaultSchedule = schedules.FirstOrDefault(schedule => schedule.IsDefault);
        if (scheduleId is { } id)
        {
            return schedules.FirstOrDefault(schedule => schedule.Id == id) is { } chosen
                ? new SupportAlertScheduleDto(ScheduleChosen, chosen.Id, chosen.Name)
                : new SupportAlertScheduleDto(ScheduleChosenMissing, defaultSchedule?.Id, defaultSchedule?.Name);
        }

        return defaultSchedule is null
            ? new SupportAlertScheduleDto(ScheduleNone, null, null)
            : new SupportAlertScheduleDto(ScheduleDefault, defaultSchedule.Id, defaultSchedule.Name);
    }

    // In the order they were saved. A member that no longer resolves (removed from the tenant) keeps its place, unnamed and unable to receive.
    private async Task<IReadOnlyList<SupportAlertRecipientDto>> RecipientsAsync(
        Guid tenantId, IReadOnlyList<Guid> recipientIds, CancellationToken cancellationToken)
    {
        if (recipientIds.Count == 0)
        {
            return [];
        }

        var found = (await members.FindAsync(tenantId, recipientIds, cancellationToken)).ToDictionary(member => member.MemberId);
        return recipientIds
            .Select(id => found.TryGetValue(id, out var member)
                ? new SupportAlertRecipientDto(id, member.DisplayName, member.IsActive && member.CanManage)
                : new SupportAlertRecipientDto(id, SupportTicketDtoComposer.UnknownName, false))
            .ToArray();
    }
}
