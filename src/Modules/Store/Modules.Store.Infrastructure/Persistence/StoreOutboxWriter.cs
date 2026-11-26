using System.Diagnostics;
using System.Text.Json;
using Modules.Store.Application;

namespace Modules.Store.Infrastructure.Persistence;

/// <summary>Copia de <c>BuyerOutboxWriter</c>: la fila queda pendiente en <see cref="StoreDbContext"/> y commitea con el resto.</summary>
internal sealed class StoreOutboxWriter(StoreDbContext dbContext) : IStoreOutbox
{
    private const int CorrelationIdMaxLength = 100;

    public void Add(string eventName, object payload, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        dbContext.Outbox.Add(new OutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload, payload.GetType(), JsonSerializerOptions.Web),
            CorrelationId = correlationId.Length > CorrelationIdMaxLength
                ? correlationId[..CorrelationIdMaxLength]
                : correlationId,
            OccurredAt = occurredAt,
            Attempts = 0,
        });
    }
}
