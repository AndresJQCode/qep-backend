using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.4: el único por <c>body_sha256</c> hace que un reenvío idéntico no cree otra fila. El cuerpo
/// ya pasó la firma; va como jsonb tal cual. <c>provider</c> (spec 2026-10-14 §6.2) dice con qué parser lo procesa el worker.</summary>
internal sealed class WebhookDeliveries(MessagingDbContext dbContext) : IWebhookDeliveries
{
    public static string ProviderColumnValue(WebhookProvider provider) => provider == WebhookProvider.Instagram ? "instagram" : "whatsapp";

    public static WebhookProvider ParseProvider(string? value) =>
        string.Equals(value, "instagram", StringComparison.Ordinal) ? WebhookProvider.Instagram : WebhookProvider.WhatsApp;

    public async Task<bool> EnqueueAsync(WebhookProvider provider, byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(body);
        var payload = Encoding.UTF8.GetString(body);
        var providerValue = ProviderColumnValue(provider);
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, attempts, provider)
            VALUES ({hash}, {payload}::jsonb, {receivedAt}, 0, {providerValue})
            ON CONFLICT (body_sha256) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }
}
