using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-26 §3.2 y §3.3 por HTTP y contra la base: bloquear y desbloquear llaman a Meta sólo cuando el estado propio
/// cambia, cada respuesta de Meta tiene su código, el conflicto después de que Meta aceptó se reintenta y nunca se deshace en Meta.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class ContactBlockApiTests
{
    private const string WaId = "573001234567";
    private const string OwnerName = "Andrea Dueña";
    private const string Added = """{"block_users":{"added_users":[{"input":"+573001234567","wa_id":"573001234567"}]}}""";
    private const string Removed = """{"block_users":{"removed_users":[{"input":"+573001234567","wa_id":"573001234567"}]}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string? waId = WaId)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", waId, "wamid.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        await ActivateAutomationAsync(connectionString, conversationId, Guid.CreateVersion7());
        await ExecuteAsync(connectionString, "UPDATE tenancy.memberships SET display_name = @name WHERE id = @id",
            ("name", OwnerName), ("id", await OwnerMembershipIdAsync(connectionString, tenant)));
        return new Fixture(factory, connectionString, tenant, conversationId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static Task<HttpResponseMessage> ActAsync(Fixture f, string action, HttpClient? client = null) =>
        SendAsync(client ?? f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/{action}");

    /// <summary>Lo que responda Meta a <c>block_users</c> desde ahora, en las dos direcciones (el fake no distingue el método).</summary>
    private static void Script(Fixture f, HttpStatusCode status, string body)
    {
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/block_users", status, body);
    }

    private static async Task BlockAsync(Fixture f)
    {
        Script(f, HttpStatusCode.OK, Added);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(f, "block")).StatusCode);
    }

    private static MetaRequest[] BlockRequests(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri?.AbsolutePath.EndsWith("/block_users", StringComparison.Ordinal) == true).ToArray();

    private static string Failed(int code) =>
        $$$"""{"block_users":{"failed_users":[{"input":"+573001234567","errors":[{"code":{{{code}}},"message":"x"}]}]}}""";

    private static Task<long> EventsAsync(Fixture f, string type) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 3 AND details->>'type' = @t", ("t", type));

    private static Task<long> BlockEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 3 AND details->>'type' IN ('ContactBlocked', 'ContactUnblocked')");

    private static Task<long> AuditAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @a", ("a", action));

    private static Task<long> BlockAuditsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action LIKE 'messaging.conversation.contact_%'");

    private static Task<string> BlockedAtAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT coalesce(blocked_at::text, '-') FROM messaging.conversations");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task BlockingCallsMetaAndMarksTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        const string untouched = "SELECT status || '|' || coalesce(assigned_member_id::text, '-') || '|' || unread_count || '|' || automation_status FROM messaging.conversations";
        var before = await ScalarAsync<string>(f.ConnectionString, untouched);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        Script(f, HttpStatusCode.OK, Added);

        var response = await ActAsync(f, "block");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blocked = (await ReadAsync(response)).GetProperty("blocked");
        Assert.NotEqual(JsonValueKind.Null, blocked.GetProperty("blockedAt").ValueKind);
        Assert.Equal(OwnerName, blocked.GetProperty("blockedByName").GetString());
        var request = Assert.Single(BlockRequests(f));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/111/block_users", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"+573001234567\"", request.Body, StringComparison.Ordinal);
        Assert.Equal(WaId, await ScalarAsync<string>(f.ConnectionString, "SELECT blocked_wa_id FROM messaging.conversations"));
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString, untouched));
        Assert.Equal(1L, await EventsAsync(f, "ContactBlocked"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.contact_blocked"));
        Assert.Equal(version + 1, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations"));
    }

    [Fact]
    public async Task BlockingAgainAnswersTheStateWithoutMetaEvenWithTheWindowClosed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await BlockAsync(f);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");

        var again = await ActAsync(f, "block");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await ReadAsync(again)).GetProperty("blocked").ValueKind);
        Assert.Single(BlockRequests(f));
        Assert.Equal(1L, await EventsAsync(f, "ContactBlocked"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.contact_blocked"));
    }

    [Fact]
    public async Task UnblockingSendsTheBlockedNumberAfterANumberChange()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await BlockAsync(f);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET wa_id = '573009999999'");
        Script(f, HttpStatusCode.OK, Removed);

        var response = await ActAsync(f, "unblock");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("blocked").ValueKind);
        var delete = Assert.Single(BlockRequests(f), request => request.Method == HttpMethod.Delete);
        Assert.Contains("\"+573001234567\"", delete.Body, StringComparison.Ordinal);
        Assert.Equal("-", await BlockedAtAsync(f));
        Assert.Equal(1L, await EventsAsync(f, "ContactUnblocked"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.contact_unblocked"));
    }

    [Fact]
    public async Task UnblockingIgnoresTheWindow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await BlockAsync(f);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        Script(f, HttpStatusCode.OK, Removed);

        var response = await ActAsync(f, "unblock");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("-", await BlockedAtAsync(f));
    }

    [Fact]
    public async Task UnblockingWhatIsNotBlockedAnswersWithoutMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        Script(f, HttpStatusCode.OK, Removed);

        var response = await ActAsync(f, "unblock");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("blocked").ValueKind);
        Assert.Empty(BlockRequests(f));
        Assert.Equal(0L, await BlockEventsAsync(f));
    }

    [Theory]
    [InlineData("no-wa-id", "messaging.contact.block_unsupported_channel")]
    [InlineData("window-closed", "messaging.contact.block_window_closed")]
    [InlineData("connection-paused", "messaging.connection_unavailable")]
    public async Task TheChecksBeforeMetaHaveTheirCodes(string scenario, string expectedCode)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, scenario == "no-wa-id" ? null : WaId);
        using var _ = f.Factory;
        Script(f, HttpStatusCode.OK, Added);
        if (scenario == "window-closed")
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");
        }
        else if (scenario == "connection-paused")
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        }

        var response = await ActAsync(f, "block");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Empty(BlockRequests(f));
        Assert.Equal("-", await BlockedAtAsync(f));
        Assert.Equal(0L, await BlockEventsAsync(f));
    }

    [Theory]
    [InlineData(200, "failed:131047", 422, "messaging.contact.block_window_closed", "Active|-")]
    [InlineData(400, "error:131047", 422, "messaging.contact.block_window_closed", "Active|-")]
    [InlineData(400, "error:130429", 502, "messaging.contact.block_failed", "Active|-")]
    [InlineData(500, "{}", 502, "messaging.contact.block_failed", "Active|-")]
    [InlineData(400, "error:190", 422, "messaging.connection_unavailable", "NeedsAttention|token_expired")]
    public async Task MetaAnswersHaveTheirCodes(int status, string answer, int expectedStatus, string expectedCode, string expectedConnection)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var body = answer.Split(':') switch
        {
            ["failed", var code] => Failed(int.Parse(code, System.Globalization.CultureInfo.InvariantCulture)),
            ["error", var code] => FakeMetaGraphHandler.GraphError(int.Parse(code, System.Globalization.CultureInfo.InvariantCulture)),
            _ => answer,
        };
        Script(f, (HttpStatusCode)status, body);

        var response = await ActAsync(f, "block");

        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(expectedConnection, await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections"));
        Assert.Equal("-", await BlockedAtAsync(f));
        Assert.Equal(0L, await BlockEventsAsync(f));
        Assert.Equal(0L, await BlockAuditsAsync(f));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("unblock")]
    public async Task AnUnrecognizedMetaCodeIs502BlockFailed(string action)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        if (action == "unblock")
        {
            await BlockAsync(f);
        }

        var blockedAt = await BlockedAtAsync(f);
        var (events, audits) = (await BlockEventsAsync(f), await BlockAuditsAsync(f));
        Script(f, HttpStatusCode.OK, Failed(139100));

        var response = await ActAsync(f, action);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("messaging.contact.block_failed", (await ProblemAsync(response)).Code);
        Assert.Equal(blockedAt, await BlockedAtAsync(f));
        Assert.Equal(events, await BlockEventsAsync(f));
        Assert.Equal(audits, await BlockAuditsAsync(f));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public async Task UnblockingRejectedByMetaIsUnblockRejected(int status)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await BlockAsync(f);
        var blockedAt = await BlockedAtAsync(f);
        Script(f, (HttpStatusCode)status, status == 200 ? Failed(131047) : FakeMetaGraphHandler.GraphError(131047));

        var response = await ActAsync(f, "unblock");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.contact.unblock_rejected", (await ProblemAsync(response)).Code);
        Assert.Equal(blockedAt, await BlockedAtAsync(f));
        Assert.Equal(0L, await EventsAsync(f, "ContactUnblocked"));
        Assert.Equal(0L, await AuditAsync(f, "messaging.conversation.contact_unblocked"));
    }

    /// <summary>Otro request escribe la conversación (<paramref name="sql"/>) mientras Meta contesta: el guardado ve una versión vieja.</summary>
    private static void ScriptWithConcurrentWrite(Fixture f, string sql, HttpStatusCode status, string body)
    {
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.RespondWith("/111/block_users", _ =>
        {
            using var connection = new NpgsqlConnection(f.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(sql, connection);
            command.ExecuteNonQuery();
            return FakeMetaGraphHandler.Json(status, body);
        });
    }

    [Fact]
    public async Task AConflictAfterMetaAcceptedIsRetriedAndBlocksOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptWithConcurrentWrite(f, "UPDATE messaging.conversations SET version = version + 1", HttpStatusCode.OK, Added);

        var response = await ActAsync(f, "block");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("-", await BlockedAtAsync(f));
        Assert.Single(BlockRequests(f));
        Assert.Equal(1L, await EventsAsync(f, "ContactBlocked"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.contact_blocked"));
    }

    // El número cambia (ContactNumberChange reescribe wa_id y sube la versión) mientras Meta bloquea: el reintento guarda el número
    // que Meta bloqueó, no el nuevo; si no, desbloquear apuntaría a un número que Meta nunca bloqueó.
    [Fact]
    public async Task ARetryAfterANumberChangeKeepsTheNumberSentToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptWithConcurrentWrite(
            f, "UPDATE messaging.conversations SET wa_id = '573009999999', version = version + 1", HttpStatusCode.OK, Added);

        var response = await ActAsync(f, "block");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("blocked").ValueKind);
        Assert.Equal(WaId, await ScalarAsync<string>(f.ConnectionString, "SELECT blocked_wa_id FROM messaging.conversations"));
        Assert.Single(BlockRequests(f));
    }

    [Fact]
    public async Task TheListAndDetailCarryBlockedAsAnExplicitNull()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var list = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        var detail = await f.Client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, f.ConversationId), Ct);

        Assert.True(list.GetProperty("items")[0].TryGetProperty("blocked", out var inList));
        Assert.Equal(JsonValueKind.Null, inList.ValueKind);
        Assert.True(detail.TryGetProperty("blocked", out var inDetail));
        Assert.Equal(JsonValueKind.Null, inDetail.ValueKind);
    }

    [Fact]
    public async Task WithoutManageItIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        Script(f, HttpStatusCode.OK, Added);
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var block = await ActAsync(f, "block", reader);
        var unblock = await ActAsync(f, "unblock", reader);

        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unblock.StatusCode);
        Assert.Empty(BlockRequests(f));
    }

    [Fact]
    public async Task AnUnknownConversationIs404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, Guid.CreateVersion7())}/block");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(response)).Code);
    }
}
