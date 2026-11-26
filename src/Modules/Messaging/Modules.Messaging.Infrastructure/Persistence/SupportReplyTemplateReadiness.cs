using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Soporte fase 2: una lectura del espejo, hermana de <see cref="OrderTemplateReadiness"/>. Sin caché: va por el índice único parcial
/// <c>UX_message_templates_support_reply</c> (a lo sumo una fila <c>SupportReply</c> por conexión).</summary>
internal sealed class SupportReplyTemplateReadiness(MessagingDbContext dbContext) : ISupportReplyTemplateReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.SupportReply && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);
}
