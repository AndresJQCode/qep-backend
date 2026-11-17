using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Soporte fase 4b: una lectura del espejo, hermana de <see cref="SupportReplyTemplateReadiness"/>. Sin caché: va por el índice único
/// parcial <c>UX_message_templates_support_survey</c> (a lo sumo una fila <c>SupportSurvey</c> por conexión).</summary>
internal sealed class SupportSurveyTemplateReadiness(MessagingDbContext dbContext) : ISupportSurveyTemplateReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.SupportSurvey && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);
}
