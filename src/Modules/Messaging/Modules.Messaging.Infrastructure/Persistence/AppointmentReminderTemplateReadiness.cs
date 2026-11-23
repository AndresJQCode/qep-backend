using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Agenda: una lectura del espejo, hermana de <see cref="SupportReplyTemplateReadiness"/>. Sin caché: va por el índice único parcial
/// <c>UX_message_templates_appointment_reminder</c> (a lo sumo una fila <c>AppointmentReminder</c> por conexión).</summary>
internal sealed class AppointmentReminderTemplateReadiness(MessagingDbContext dbContext) : IAppointmentReminderReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.AppointmentReminder && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AppointmentReminderTemplateStatus?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.AppointmentReminder)
            .Select(template => new AppointmentReminderTemplateStatus(template.Status, template.StatusReason))
            .FirstOrDefaultAsync(cancellationToken);
}
