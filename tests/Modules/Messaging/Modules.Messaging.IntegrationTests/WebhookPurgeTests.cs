using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: las entregas procesadas con más de 7 días se borran en lotes; las pendientes y las recientes se quedan.</summary>
public sealed class WebhookPurgeTests
{
    [Fact]
    public async Task OnlyOldProcessedDeliveriesAreDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
        await ExecuteAsync(connectionString, """
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, processed_at) VALUES
              (decode('01', 'hex'), '{}', now() - interval '10 days', now() - interval '8 days'),
              (decode('02', 'hex'), '{}', now() - interval '10 days', now() - interval '6 days'),
              (decode('03', 'hex'), '{}', now() - interval '10 days', NULL)
            """);

        await DrainPurgeAsync(factory);

        Assert.Equal("02,03", await ScalarAsync<string>(connectionString, "SELECT string_agg(encode(body_sha256, 'hex'), ',' ORDER BY id) FROM messaging.webhook_deliveries"));
    }
}
