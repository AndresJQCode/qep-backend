using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29: Messaging consume el evento de conexión por coexistencia y deja la importación del historial en
/// <c>Pending</c>, con la hora de conexión del evento.</summary>
public sealed class CoexistenceEventsTests
{
    private const string ConnectedEvent = "integrations.whatsapp-coexistence-connected.v1";

    [Fact]
    public async Task TheEventCreatesAPendingSyncWithTheConnectionTime()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var connectedAt = new DateTimeOffset(2026, 10, 29, 15, 30, 0, TimeSpan.Zero);
        await InsertEventAsync(connectionString, tenantId, connectionId, connectedAt);

        await DrainCoexistenceEventsAsync(factory);

        Assert.Equal(
            $"{tenantId}|Pending|Pending",
            await ScalarAsync<string>(
                connectionString,
                "SELECT tenant_id || '|' || contacts_state || '|' || history_state FROM messaging.history_syncs WHERE connection_id = @id",
                ("id", connectionId)));
        Assert.Equal(
            connectedAt.UtcDateTime,
            await ScalarAsync<DateTime>(connectionString, "SELECT connected_at AT TIME ZONE 'UTC' FROM messaging.history_syncs WHERE connection_id = @id", ("id", connectionId)));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.inbox_messages WHERE consumer = 'messaging.coexistence-events' AND processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task ProcessingTheSameEventTwiceKeepsOneRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var connectedAt = new DateTimeOffset(2026, 10, 29, 15, 30, 0, TimeSpan.Zero);
        await InsertEventAsync(connectionString, tenantId, connectionId, connectedAt);
        await DrainCoexistenceEventsAsync(factory);

        // Un evento repetido llega con otra hora: la fila ya creada no se toca.
        await ExecuteAsync(connectionString, "DELETE FROM messaging.inbox_messages WHERE consumer = 'messaging.coexistence-events'");
        await ExecuteAsync(
            connectionString,
            "UPDATE platform.outbox_messages SET payload = jsonb_set(payload, '{occurredAt}', to_jsonb(@later::text)) WHERE event_name = @eventName",
            ("later", connectedAt.AddHours(5).ToString("O")), ("eventName", ConnectedEvent));
        await DrainCoexistenceEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.history_syncs"));
        Assert.Equal(
            connectedAt.UtcDateTime,
            await ScalarAsync<DateTime>(connectionString, "SELECT connected_at AT TIME ZONE 'UTC' FROM messaging.history_syncs WHERE connection_id = @id", ("id", connectionId)));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.inbox_messages WHERE consumer = 'messaging.coexistence-events' AND processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AMalformedPayloadIsMarkedProcessedWithoutARow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        await InsertRawEventAsync(connectionString, """{"tenantId":"not-a-guid","connectionId":null}""");

        await DrainCoexistenceEventsAsync(factory);

        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.history_syncs"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.inbox_messages WHERE consumer = 'messaging.coexistence-events' AND processed_at IS NOT NULL"));
    }

    private static Task InsertEventAsync(string connectionString, Guid tenantId, Guid connectionId, DateTimeOffset occurredAt) =>
        InsertRawEventAsync(
            connectionString,
            JsonSerializer.Serialize(new { tenantId, connectionId, providerKey = "whatsapp", occurredAt }),
            occurredAt);

    // La fila que escribe IntegrationsEventPublisher en la misma transacción del cambio.
    private static async Task InsertRawEventAsync(string connectionString, string payload, DateTimeOffset? occurredAt = null) =>
        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, 'coexistence-events-tests', @occurredAt, now(), 0)
            """,
            ("id", Guid.NewGuid()), ("eventName", ConnectedEvent), ("payload", payload), ("occurredAt", occurredAt ?? DateTimeOffset.UtcNow)));
}
