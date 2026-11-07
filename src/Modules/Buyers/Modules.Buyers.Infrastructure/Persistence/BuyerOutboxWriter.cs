using System.Diagnostics;
using System.Text.Json;
using Modules.Buyers.Application;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerOutboxWriter(BuyersDbContext dbContext) : IBuyerOutbox
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
