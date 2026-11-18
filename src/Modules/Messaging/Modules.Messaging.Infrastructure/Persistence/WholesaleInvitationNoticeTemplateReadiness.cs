using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Mayorista: una lectura del espejo, hermana de <see cref="SupportReplyTemplateReadiness"/>. Sin caché: va por el índice único parcial
/// <c>UX_message_templates_wholesale_invitation_notice</c> (a lo sumo una fila <c>WholesaleInvitationNotice</c> por conexión).</summary>
internal sealed class WholesaleInvitationNoticeTemplateReadiness(MessagingDbContext dbContext) : IWholesaleInvitationNoticeReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.WholesaleInvitationNotice && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);
}
