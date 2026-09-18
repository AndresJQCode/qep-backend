using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Application;

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

internal sealed partial class QuotationExpirationProcessor(
    QuotationsDbContext dbContext,
    IQuotationAuditPublisher auditPublisher,
    IClock clock,
    ITenantClock tenantClock,
    ILogger<QuotationExpirationProcessor> logger) : IQuotationExpirationProcessor
{
    // Sin actor humano detrás del vencimiento automático: Guid.Empty es el sentinela de "sistema"
    // para el actorId que ICatalogAuditPublisher-style publishers ya exigen.
    private static readonly Guid SystemActorId = Guid.Empty;

    public async Task<int> ExpirePastDueQuotationsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // "Hoy" es el día del tenant (spec 2026-09-17, punto 1). Ningún huso adelanta más de un día a
        // UTC, así que lo vencido para cualquier tenant vence antes de hoyUTC + 2: la consulta amplia
        // usa el índice, y el corte fino se hace en memoria con el calendario de cada tenant.
        var ceiling = DateOnly.FromDateTime(now.UtcDateTime).AddDays(2);
        var candidates = await dbContext.Quotations
            .Where(quotation => quotation.Status == QuotationStatus.Sent)
            .Where(quotation => quotation.ValidUntil != null && quotation.ValidUntil < ceiling)
            .ToListAsync(cancellationToken);

        var expired = 0;
        // Un calendario por tenant, no uno por fila.
        foreach (var byTenant in candidates.GroupBy(quotation => quotation.TenantId))
        {
            TenantCalendar calendar;
            try
            {
                calendar = await tenantClock.GetAsync(byTenant.Key, cancellationToken);
            }
            catch (Exception exception) when (
                exception is ResourceNotFoundException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Decisión del owner (2026-09-17): sin huso no se adivina el día, ni con UTC por
                // defecto. Se salta el tenant y se registra; los demás tenants siguen venciendo.
                LogTenantSkipped(logger, exception, byTenant.Key, byTenant.Count());
                continue;
            }

            foreach (var quotation in byTenant.Where(quotation => quotation.ValidUntil < calendar.Today))
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
                expired++;
            }
        }

        if (expired > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return expired;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Quotation expiration skipped tenant {TenantId}: its time zone could not be resolved. {CandidateCount} candidate quotations were left untouched.")]
    private static partial void LogTenantSkipped(ILogger logger, Exception exception, Guid tenantId, int candidateCount);
}
