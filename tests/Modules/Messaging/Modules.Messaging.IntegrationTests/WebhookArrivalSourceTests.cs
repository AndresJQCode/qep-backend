using Bootstrapper.Observability;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
        // Relativas a ahora y en segundos enteros: WebhookPurgeWorker borra al arrancar lo procesado hace más de
        // DeliveryRetentionDays, así que una fecha fija dejaría la prueba en rojo una semana después.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var newestWhatsApp = now.AddMinutes(-1);
        var instagram = now.AddMinutes(-30);
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

    /// <summary>Sin el índice, un proveedor sin filas (Instagram sin conexiones) recorre la tabla entera cada 15 s.</summary>
    [Fact]
    public async Task TheLastReceivedQueryUsesTheProviderIndex()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var host = new QepApiFactory(connectionString);
        _ = host.Services; // arranca el host: migra el esquema.

        // Con pocas filas el planificador prefiere el barrido; apagarlo deja ver si la consulta puede usar el índice.
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(Ct);
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", db))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand("EXPLAIN " + WebhookArrivalSource.LastReceivedSql, db);
        explain.Parameters.AddWithValue("provider", "instagram");
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains(plan, line => line.Contains("IX_webhook_deliveries_provider_id", StringComparison.Ordinal));
    }
}
