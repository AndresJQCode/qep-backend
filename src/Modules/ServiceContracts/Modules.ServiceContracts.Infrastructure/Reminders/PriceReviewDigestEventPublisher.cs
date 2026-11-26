using System.Globalization;
using System.Text.Json;
using Modules.ServiceContracts.Infrastructure.Persistence;

namespace Modules.ServiceContracts.Infrastructure.Reminders;

/// <summary>
/// Spec reajuste §6.6: «tienes precios por revisar o cobros retenidos» para la campana de un miembro que recibe el digest diario.
/// Se escribe y se guarda igual que <see cref="OverdueDigestEventPublisher"/>: justo después de ganar el claim del digest y antes
/// de mirar el correo, así un miembro sin correo igual lo ve. Un claim retomado con el lease vencido publica de nuevo y la
/// <c>dedupe_key</c> del día del consumidor absorbe la repetición.
/// </summary>
internal sealed class PriceReviewDigestEventPublisher(ServiceContractsDbContext dbContext)
{
    public const string EventName = "billing.price-reviews.digest.v1";

    public async Task PublishAsync(
        Guid tenantId,
        Guid memberId,
        DateOnly runDate,
        int dueCount,
        int heldCount,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var date = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        dbContext.Outbox.Add(new ServiceContractsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = JsonSerializer.Serialize(new DigestPayload(tenantId, memberId, date, dueCount, heldCount, occurredAt)),
            CorrelationId = $"{tenantId:D}:{memberId:D}:{date}",
            OccurredAt = occurredAt,
            Attempts = 0,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Los nombres camelCase son el contrato con Notifications (se leen por nombre); System.Text.Json escribe los parámetros de un
    // record tal cual.
    private sealed record DigestPayload(
        Guid tenantId, Guid memberId, string runDate, int dueCount, int heldCount, DateTimeOffset occurredAt);
}
