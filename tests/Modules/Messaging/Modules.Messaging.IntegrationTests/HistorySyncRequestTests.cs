using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29: el worker reclama las importaciones con un tipo <c>Pending</c> dentro de las 24 horas de la conexión y
/// le pide a Meta los contactos (<c>smb_app_state_sync</c>) y después el historial (<c>history</c>) por <c>smb_app_data</c>.</summary>
public sealed class HistorySyncRequestTests
{
    private const string PhoneNumberId = "111";
    private const string SmbAppDataPath = "/111/smb_app_data";
    private const string ConnectedEvent = "integrations.whatsapp-coexistence-connected.v1";

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, Guid TenantId, Guid ConnectionId)
        : IDisposable
    {
        public void Dispose()
        {
            if (!ReferenceEquals(Host, Factory))
            {
                Host.Dispose();
            }

            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, TestClock? clock = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        WebApplicationFactory<Program> host = clock is null ? factory : factory.WithTestClock(clock);
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        return new Fixture(factory, host, connectionString, tenant.TenantId, connectionId);
    }

    private static async Task InsertSyncAsync(
        Fixture f, DateTimeOffset connectedAt, string contactsState = "Pending", string historyState = "Pending", string? contactsRequestId = null) =>
        Assert.Equal(1, await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, contacts_request_id, history_state, attempts, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, @connectedAt, @contactsState, @contactsRequestId, @historyState, 0, now(), now(), 1)
            """,
            ("connectionId", f.ConnectionId), ("tenantId", f.TenantId), ("connectedAt", connectedAt),
            ("contactsState", contactsState), ("contactsRequestId", (object?)contactsRequestId ?? DBNull.Value), ("historyState", historyState)));

    /// <summary>Responde en orden: la primera llamada a <c>smb_app_data</c> recibe <paramref name="answers"/>[0], la segunda [1]…</summary>
    private static void AnswerSmbAppData(Fixture f, params (HttpStatusCode Status, string Body)[] answers)
    {
        var calls = 0;
        f.Factory.MetaHandler.RespondWith(SmbAppDataPath, _ =>
        {
            var index = Math.Min(Interlocked.Increment(ref calls), answers.Length) - 1;
            return FakeMetaGraphHandler.Json(answers[index].Status, answers[index].Body);
        });
    }

    private static List<MetaRequest> SmbAppDataRequests(Fixture f) =>
        [.. f.Factory.MetaHandler.Requests.Where(request => request.Uri?.AbsolutePath.EndsWith(SmbAppDataPath, StringComparison.Ordinal) == true)];

    private static Task<string> RowAsync(Fixture f) =>
        ScalarAsync<string>(
            f.ConnectionString,
            """
            SELECT contacts_state || '|' || coalesce(contacts_request_id, '-') || '|' || coalesce(contacts_error_code, '-') || '|'
                || history_state || '|' || coalesce(history_request_id, '-') || '|' || coalesce(history_error_code, '-')
              FROM messaging.history_syncs WHERE connection_id = @id
            """,
            ("id", f.ConnectionId));

    [Fact]
    public async Task APendingSyncRequestsContactsThenHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-1));
        AnswerSmbAppData(f, (HttpStatusCode.OK, """{"request_id":"R-C"}"""), (HttpStatusCode.OK, """{"request_id":"R-H"}"""));

        await DrainHistorySyncRequestsAsync(f.Host);

        var requests = SmbAppDataRequests(f);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(HttpMethod.Post, request.Method));
        Assert.All(requests, request => Assert.Equal($"Bearer {SentinelMetaAccessToken}", request.Authorization));
        Assert.Equal("""{"messaging_product":"whatsapp","sync_type":"smb_app_state_sync"}""", requests[0].Body);
        Assert.Equal("""{"messaging_product":"whatsapp","sync_type":"history"}""", requests[1].Body);
        Assert.Equal("Requested|R-C|-|Requested|R-H|-", await RowAsync(f));
        Assert.Equal(
            1L,
            await CountAsync(
                f.ConnectionString,
                "SELECT count(*) FROM messaging.history_syncs WHERE contacts_requested_at IS NOT NULL AND history_requested_at IS NOT NULL"));
    }

    // Spec §8, «sobrevive a una caída»: sólo existe la fila del outbox (la que Integrations commitea con la conexión).
    [Fact]
    public async Task ACommittedEventIsRequestedEvenIfNothingRanAfterTheCommit()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        Assert.Equal(1, await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, 'history-sync-request-tests', now(), now(), 0)
            """,
            ("id", Guid.NewGuid()), ("eventName", ConnectedEvent),
            ("payload", JsonSerializer.Serialize(new { tenantId = f.TenantId, connectionId = f.ConnectionId, providerKey = "whatsapp", occurredAt = DateTimeOffset.UtcNow }))));
        AnswerSmbAppData(f, (HttpStatusCode.OK, """{"request_id":"R-C"}"""), (HttpStatusCode.OK, """{"request_id":"R-H"}"""));

        await DrainCoexistenceEventsAsync(f.Host);
        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Equal("Requested|R-C|-|Requested|R-H|-", await RowAsync(f));
    }

    [Fact]
    public async Task AGraphRejectionLeavesTheTypeFailedWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-1));
        AnswerSmbAppData(f, (HttpStatusCode.OK, """{"request_id":"R-C"}"""), (HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100)));

        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Equal("Requested|R-C|-|Failed|-|graph:100", await RowAsync(f));
    }

    [Fact]
    public async Task AGraphRejectionWithoutCodeKeepsTheHttpStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-1));
        AnswerSmbAppData(f, (HttpStatusCode.Forbidden, "{}"));

        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Equal("Failed|-|graph:403|Failed|-|graph:403", await RowAsync(f));
    }

    // Review Focus 1: un timeout no se pierde en silencio. Queda Pending con «unconfirmed», no se repite mientras dura el reclamo y,
    // vencido, se vuelve a pedir.
    [Fact]
    public async Task AnUnconfirmedRequestStaysPendingAndIsRetried()
    {
        await using var database = await StartDatabaseAsync();
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        using var f = await ArrangeAsync(database, clock);
        await InsertSyncAsync(f, clock.UtcNow.AddHours(-1));
        f.Factory.MetaHandler.Throw = new TaskCanceledException("timed out", new TimeoutException());

        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Equal("Pending|-|unconfirmed|Pending|-|unconfirmed", await RowAsync(f));
        Assert.Equal(2, SmbAppDataRequests(f).Count);

        f.Factory.MetaHandler.Throw = null;
        AnswerSmbAppData(f, (HttpStatusCode.OK, """{"request_id":"R-C"}"""), (HttpStatusCode.OK, """{"request_id":"R-H"}"""));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await DrainHistorySyncRequestsAsync(f.Host);
        Assert.Equal(2, SmbAppDataRequests(f).Count);

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Equal(4, SmbAppDataRequests(f).Count);
        Assert.Equal("Requested|R-C|-|Requested|R-H|-", await RowAsync(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT attempts::bigint FROM messaging.history_syncs"));
    }

    [Fact]
    public async Task APausedConnectionStaysPendingWithoutCallingGraph()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-1));
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Empty(SmbAppDataRequests(f));
        Assert.Equal("Pending|-|connection_unavailable|Pending|-|connection_unavailable", await RowAsync(f));
    }

    [Fact]
    public async Task AnExpiredSyncIsNeverRequested()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-25));

        await DrainHistorySyncRequestsAsync(f.Host);

        Assert.Empty(SmbAppDataRequests(f));
        Assert.Equal("Pending|-|-|Pending|-|-", await RowAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT attempts::bigint FROM messaging.history_syncs"));
    }

    // Los contactos ya pedidos y el historial devuelto a Pending por Retry (sin código de error): sólo se pide el historial.
    [Fact]
    public async Task ARequestedTypeIsNeverRequestedAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InsertSyncAsync(f, DateTimeOffset.UtcNow.AddHours(-1), contactsState: "Requested", contactsRequestId: "R-C");
        AnswerSmbAppData(f, (HttpStatusCode.OK, """{"request_id":"R-H"}"""));

        await DrainHistorySyncRequestsAsync(f.Host);

        var requests = SmbAppDataRequests(f);
        Assert.Equal("""{"messaging_product":"whatsapp","sync_type":"history"}""", Assert.Single(requests).Body);
        Assert.Equal("Requested|R-C|-|Requested|R-H|-", await RowAsync(f));
    }
}
