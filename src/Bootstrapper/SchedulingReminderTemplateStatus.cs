using Modules.Messaging.Application;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-23 §2: the adapter of <see cref="ISchedulingReminderTemplateStatus"/> towards Messaging, sibling of
/// <see cref="SchedulingReminderSender"/>. Messaging answers Meta's raw status of the <c>AppointmentReminder</c> row of the connection (at most
/// one per connection); no row, or a tenant without the messaging module, is <c>Missing</c> (the Agenda does not need Messaging to exist).
/// </summary>
internal sealed class SchedulingReminderTemplateStatus(IAppointmentReminderReadiness readiness, ITenantModules tenantModules)
    : ISchedulingReminderTemplateStatus
{
    public async Task<ReminderTemplateStateDto> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Messaging))
        {
            return ReminderTemplateStates.Missing;
        }

        return await readiness.FindStatusAsync(tenantId, connectionId, cancellationToken) is { } row
            ? new ReminderTemplateStateDto(row.Status, row.StatusReason)
            : ReminderTemplateStates.Missing;
    }
}
