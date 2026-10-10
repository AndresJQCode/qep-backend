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

    // El borde: «más de 7 días» es processed_at < cutoff; la que cae justo en el cutoff se queda.
    [Fact]
    public async Task ADeliveryProcessedExactlyAtTheCutoffStays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero) };
        using var baseFactory = new QepApiFactory(connectionString);
        using var factory = baseFactory.WithTestClock(clock);
        _ = factory.Services;
        var cutoff = clock.UtcNow.AddDays(-7);
        await ExecuteAsync(connectionString, """
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, processed_at) VALUES
              (decode('01', 'hex'), '{}', @cutoff - interval '1 day', @cutoff - interval '1 microsecond'),
              (decode('02', 'hex'), '{}', @cutoff - interval '1 day', @cutoff)
            """, ("cutoff", cutoff));

        await DrainPurgeAsync(factory);

        Assert.Equal("02", await ScalarAsync<string>(connectionString, "SELECT string_agg(encode(body_sha256, 'hex'), ',' ORDER BY id) FROM messaging.webhook_deliveries"));
    }
}
