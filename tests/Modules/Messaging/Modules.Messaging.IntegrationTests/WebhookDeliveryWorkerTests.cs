using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: una ráfaga no se procesa a razón de un lote por tick. Mientras el lote
/// reclamado vuelve lleno, la misma pasada sigue drenando (con tope por tick).</summary>
public sealed class WebhookDeliveryWorkerTests
{
    [Fact]
    public async Task OneTickDrainsMoreThanOneFullBatch()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        // Reloj quieto: lo reclamado y no procesado no vuelve a salir en la misma pasada.
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero) };
        using var baseFactory = new QepApiFactory(connectionString);
        using var factory = baseFactory.WithTestClock(clock);
        _ = factory.Services;
        // 120 entregas sin cambios: cada una se procesa en el acto. El lote es de 50.
        await ExecuteAsync(connectionString, """
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, attempts)
            SELECT sha256(convert_to(i::text, 'UTF8')), '{}', @now, 0 FROM generate_series(1, 120) AS i
            """, ("now", clock.UtcNow));

        await DrainDeliveriesAsync(factory);

        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));
        Assert.Equal(120L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }
}
