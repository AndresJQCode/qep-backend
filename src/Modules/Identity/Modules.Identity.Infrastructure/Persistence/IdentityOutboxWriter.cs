using System.Text.Json;
using Modules.Identity.Application;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityOutboxWriter(IdentityDbContext dbContext) : IIdentityOutbox
{
    private const int CorrelationIdMaxLength = 100;

    public void Add(string eventName, object payload, string correlationId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
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
