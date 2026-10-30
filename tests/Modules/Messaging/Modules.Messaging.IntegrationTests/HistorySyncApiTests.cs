using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 §5: el estado de la importación del historial de una conexión y su reintento, con los permisos de la
/// conexión (P-8) y sin el guard del módulo de Messaging.</summary>
public sealed class HistorySyncApiTests
{
    private const string ReadPermission = "integrations.connection.read";
    private const string ManagePermission = "integrations.connection.manage";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(Guid tenantId, Guid connectionId) =>
        $"/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/history-sync";

    /// <summary>Al segundo: <c>timestamptz</c> guarda microsegundos y la comparación del plazo tiene que ser exacta.</summary>
    private static DateTimeOffset NowToTheSecond()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static Task<int> SeedSyncAsync(
        string connectionString,
        Guid tenantId,
        Guid connectionId,
        DateTimeOffset connectedAt,
        string contactsState = "Pending",
        DateTimeOffset? contactsRequestedAt = null,
        string historyState = "Pending",
        string? historyErrorCode = null,
        DateTimeOffset? claimedUntil = null) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, contacts_request_id, contacts_requested_at,
                 history_state, history_error_code, claimed_until, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, @connectedAt, @contactsState,
                    CASE WHEN @contactsState = 'Requested' THEN 'req-contacts' END, @contactsRequestedAt,
                    @historyState, @historyErrorCode, @claimedUntil, @connectedAt, @connectedAt, 1)
            """,
            ("connectionId", connectionId),
            ("tenantId", tenantId),
            ("connectedAt", connectedAt),
            ("contactsState", contactsState),
            ("contactsRequestedAt", (object?)contactsRequestedAt ?? DBNull.Value),
            ("historyState", historyState),
            ("historyErrorCode", (object?)historyErrorCode ?? DBNull.Value),
            ("claimedUntil", (object?)claimedUntil ?? DBNull.Value));

    [Fact]
    public async Task TheStateComesComputed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var connectedAt = NowToTheSecond().AddHours(-1);
        await SeedSyncAsync(connectionString, tenantId, connectionId, connectedAt,
            contactsState: "Requested", contactsRequestedAt: connectedAt.AddMinutes(1), historyState: "Failed", historyErrorCode: "graph:100");
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, ReadPermission);

        var response = await client.GetAsync(Url(tenantId, connectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(connectionId, body.GetProperty("connectionId").GetGuid());
        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal(connectedAt, body.GetProperty("connectedAt").GetDateTimeOffset());
        Assert.Equal(connectedAt.AddHours(24), body.GetProperty("deadline").GetDateTimeOffset());
        var contacts = body.GetProperty("contacts");
        Assert.Equal("Requested", contacts.GetProperty("state").GetString());
        Assert.Equal(connectedAt.AddMinutes(1), contacts.GetProperty("requestedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, contacts.GetProperty("errorCode").ValueKind);
        var history = body.GetProperty("history");
        Assert.Equal("Failed", history.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("requestedAt").ValueKind);
        Assert.Equal("graph:100", history.GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("progress").ValueKind);
        Assert.True(body.GetProperty("canRetry").GetBoolean());
        Assert.False(body.GetProperty("mustReconnect").GetBoolean());
    }

    [Fact]
    public async Task AnExpiredSyncAsksToReconnect()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenantId, connectionId, NowToTheSecond().AddHours(-25));
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, ReadPermission);

        var body = await client.GetFromJsonAsync<JsonElement>(Url(tenantId, connectionId), Ct);

        Assert.Equal("Expired", body.GetProperty("status").GetString());
        Assert.Equal("Pending", body.GetProperty("history").GetProperty("state").GetString());
        Assert.False(body.GetProperty("canRetry").GetBoolean());
        Assert.True(body.GetProperty("mustReconnect").GetBoolean());
    }

    [Fact]
    public async Task RetryMovesFailedToPendingAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = Guid.CreateVersion7();
        var connectedAt = NowToTheSecond().AddHours(-1);
        await SeedSyncAsync(connectionString, tenant.TenantId, connectionId, connectedAt,
            contactsState: "Requested", contactsRequestedAt: connectedAt.AddMinutes(1), historyState: "Failed", historyErrorCode: "graph:100",
            claimedUntil: DateTimeOffset.UtcNow.AddMinutes(2));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermission, ManagePermission);

        var response = await client.PostAsync($"{Url(tenant.TenantId, connectionId)}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal("Pending", body.GetProperty("history").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("history").GetProperty("errorCode").ValueKind);
        Assert.Equal("Requested", body.GetProperty("contacts").GetProperty("state").GetString());
        Assert.Equal("Pending|<none>|<free>|2", await ScalarAsync<string>(connectionString,
            "SELECT history_state || '|' || coalesce(history_error_code, '<none>') || '|' || CASE WHEN claimed_until IS NULL THEN '<free>' ELSE 'claimed' END || '|' || version FROM messaging.history_syncs WHERE connection_id = @id",
            ("id", connectionId)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = 'messaging.history_sync.retried' AND resource_id = @id AND tenant_id = @tenantId AND changed_fields::text = '[\"History\"]'",
            ("id", connectionId.ToString()), ("tenantId", tenant.TenantId)));
    }

    [Fact]
    public async Task RetryAfterTheDeadlineIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenantId, connectionId, NowToTheSecond().AddHours(-25), historyState: "Failed", historyErrorCode: "graph:100");
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, ManagePermission);

        var response = await client.PostAsync($"{Url(tenantId, connectionId)}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.history_sync.retry_not_allowed", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.history_sync.retried'"));
    }

    [Fact]
    public async Task AConnectionWithoutImportIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, ReadPermission, ManagePermission);

        var get = await client.GetAsync(Url(tenantId, Guid.CreateVersion7()), Ct);
        var retry = await client.PostAsync($"{Url(tenantId, Guid.CreateVersion7())}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("messaging.history_sync.not_found", (await ProblemAsync(get)).Code);
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        Assert.Equal("messaging.history_sync.not_found", (await ProblemAsync(retry)).Code);
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenantB, connectionId, NowToTheSecond().AddHours(-1), historyState: "Failed", historyErrorCode: "graph:100");
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantA, ReadPermission, ManagePermission);

        var get = await client.GetAsync(Url(tenantB, connectionId), Ct);
        var retry = await client.PostAsync($"{Url(tenantB, connectionId)}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(get)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(retry)).Code);
        Assert.Equal("Failed", await ScalarAsync<string>(connectionString,
            "SELECT history_state FROM messaging.history_syncs WHERE connection_id = @id", ("id", connectionId)));
    }

    [Fact]
    public async Task WithoutConnectionReadIs403Not500()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenantId, connectionId, NowToTheSecond().AddHours(-1));
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, "messaging.conversation.read");

        var response = await client.GetAsync(Url(tenantId, connectionId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RetryNeedsConnectionManage()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenantId, connectionId, NowToTheSecond().AddHours(-1), historyState: "Failed", historyErrorCode: "graph:100");
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, ReadPermission);

        var response = await client.PostAsync($"{Url(tenantId, connectionId)}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Failed", await ScalarAsync<string>(connectionString,
            "SELECT history_state FROM messaging.history_syncs WHERE connection_id = @id", ("id", connectionId)));
    }

    // P-8: la importación es de la pantalla de la conexión y D-5 la procesa con el módulo apagado; el tenant sin fila en
    // tenancy.tenant_modules tiene Messaging apagado.
    [Fact]
    public async Task TheStateIsReadableWithMessagingOff()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM tenancy.tenant_modules WHERE tenant_id = @t AND module_key = 'messaging' AND status = 'active'",
            ("t", tenant.TenantId)));
        var connectionId = Guid.CreateVersion7();
        await SeedSyncAsync(connectionString, tenant.TenantId, connectionId, NowToTheSecond().AddHours(-1));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermission);

        var response = await client.GetAsync(Url(tenant.TenantId, connectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
    }
}
