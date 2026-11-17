using Bootstrapper.Observability;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-16: la hora del último webhook de Meta por proveedor, leída con SQL crudo de
/// <c>messaging.webhook_deliveries</c> contra el esquema migrado. Un proveedor sin entregas no aparece.</summary>
public sealed class WebhookArrivalSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<int> DeliveryAsync(string connectionString, string provider, DateTimeOffset receivedAt) =>
        ExecuteAsync(connectionString, """
            INSERT INTO messaging.webhook_deliveries (provider, body_sha256, payload, received_at, attempts, processed_at)
            VALUES (@provider, sha256(convert_to(gen_random_uuid()::text, 'UTF8')), '{}', @at, 0, @at)
            """, ("provider", provider), ("at", receivedAt));

    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadAsync(QepApiFactory host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWebhookArrivalSource>().ReadLastReceivedAsync(Ct);
    }

    [Fact]
    public async Task ReturnsTheNewestDeliveryPerProvider()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var host = new QepApiFactory(connectionString);
        var newestWhatsApp = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        var instagram = new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero);
        await DeliveryAsync(connectionString, "whatsapp", newestWhatsApp.AddMinutes(-10));
        await DeliveryAsync(connectionString, "instagram", instagram);
        await DeliveryAsync(connectionString, "whatsapp", newestWhatsApp);

        var lastReceived = await ReadAsync(host);

        Assert.Equal(newestWhatsApp, lastReceived["whatsapp"]);
        Assert.Equal(instagram, lastReceived["instagram"]);
    }

    [Fact]
    public async Task AProviderWithoutDeliveriesIsAbsent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var host = new QepApiFactory(connectionString);
        await DeliveryAsync(connectionString, "whatsapp", DateTimeOffset.UtcNow);

        var lastReceived = await ReadAsync(host);

        Assert.True(lastReceived.ContainsKey("whatsapp"));
        Assert.False(lastReceived.ContainsKey("instagram"));
    }
}
