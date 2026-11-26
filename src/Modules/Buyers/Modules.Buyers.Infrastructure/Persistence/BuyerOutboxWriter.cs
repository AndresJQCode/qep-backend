using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Los correos que un borrado deja sin sentido. No se mira si Notifications ya los procesó: Buyers no lee su inbox, y una
    /// fila procesada sólo esperaba la purga por edad de <see cref="BuyerHousekeepingWorker"/>, que pasa por esta misma tabla.
    /// La cuenta se compara contra <c>accountId</c> del payload (<see cref="BuyerEmailPayload"/>, serializado en camelCase).
    /// </summary>
    public Task RemoveAccountEmailsAsync(Guid accountId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM platform.outbox_messages
            WHERE event_name = ANY({AccountEmailEvents})
              AND payload->>'accountId' = {accountId.ToString()}
            """,
            cancellationToken);

    private static readonly string[] AccountEmailEvents =
    [
        BuyerIntegrationEvents.EmailVerificationRequested,
        BuyerIntegrationEvents.ExistingAccountNotice,
        BuyerIntegrationEvents.PasswordResetRequested,
    ];
}
