using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.4: el único por <c>body_sha256</c> hace que un reenvío idéntico no cree otra fila. El cuerpo
/// ya pasó la firma; va como jsonb tal cual.</summary>
internal sealed class WebhookDeliveries(MessagingDbContext dbContext) : IWebhookDeliveries
{
    public async Task<bool> EnqueueAsync(byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(body);
        var payload = Encoding.UTF8.GetString(body);
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, attempts)
            VALUES ({hash}, {payload}::jsonb, {receivedAt}, 0)
            ON CONFLICT (body_sha256) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }
}
