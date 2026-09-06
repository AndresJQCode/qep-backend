using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure.Expiration;

/// <summary>
/// US-19: mueve a <see cref="QuotationStatus.Expired"/> las cotizaciones <c>Sent</c> cuya
/// <c>ValidUntil</c> ya pasó. <c>internal</c>, igual que <c>IOutboxProcessor</c> en Tenancy — es
/// mecanismo de infraestructura, no un puerto que otro módulo consuma. Visible además para
/// <c>Modules.Quotations.IntegrationTests</c> (ver <c>InternalsVisibleTo</c> en el csproj): las
/// pruebas de integración lo invocan directo para no depender del temporizador del worker.
/// </summary>
internal interface IQuotationExpirationProcessor
{
    Task<int> ExpirePastDueQuotationsAsync(CancellationToken cancellationToken);
}

internal sealed class QuotationExpirationProcessor(
    QuotationsDbContext dbContext,
    IQuotationAuditPublisher auditPublisher,
    IClock clock) : IQuotationExpirationProcessor
{
    // Sin actor humano detrás del vencimiento automático: Guid.Empty es el sentinela de "sistema"
    // para el actorId que ICatalogAuditPublisher-style publishers ya exigen.
    private static readonly Guid SystemActorId = Guid.Empty;

    public async Task<int> ExpirePastDueQuotationsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var expirable = await dbContext.Quotations
            .Where(quotation => quotation.Status == QuotationStatus.Sent)
            .Where(quotation => quotation.ValidUntil != null && quotation.ValidUntil < today)
            .ToListAsync(cancellationToken);

        foreach (var quotation in expirable)
        {
            quotation.Expire(now);
            dbContext.QuotationHistoryEntries.Add(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Expired,
                memberId: null,
                QuotationChangeSummary.Expired(),
                now));
            auditPublisher.Publish(
                quotation.TenantId,
                SystemActorId,
                "quotation.quotation.expired",
                quotation.Id.ToString(),
                "success",
                now);
        }

        if (expirable.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return expirable.Count;
    }
}
