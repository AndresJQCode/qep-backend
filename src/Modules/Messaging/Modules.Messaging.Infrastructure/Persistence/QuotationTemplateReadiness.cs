using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-14 §4.3 y §5: una lectura del espejo. Sin caché: va por el índice único parcial
/// <c>UX_message_templates_quotation</c> (a lo sumo una fila <c>Quotation</c> por conexión) y la respuesta decide si una cotización se puede
/// enviar por Meta.</summary>
internal sealed class QuotationTemplateReadiness(MessagingDbContext dbContext) : IQuotationTemplateReadiness
{
    public Task<ApprovedQuotationTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.Quotation && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedQuotationTemplate(template.Name, template.Language))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Cobros recurrentes §5: una lectura del espejo, hermana de <see cref="QuotationTemplateReadiness"/>. Sin caché: va por el índice único
/// parcial <c>UX_message_templates_billing</c> (a lo sumo una fila por tipo de aviso y conexión).</summary>
internal sealed class BillingTemplateReadiness(MessagingDbContext dbContext) : IBillingTemplateReadiness
{
    public Task<ApprovedBillingTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, MessageTemplateKind kind, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == kind && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedBillingTemplate(template.Name, template.Language))
            .FirstOrDefaultAsync(cancellationToken);
}
