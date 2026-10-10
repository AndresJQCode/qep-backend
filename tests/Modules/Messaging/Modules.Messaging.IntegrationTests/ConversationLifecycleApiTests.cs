using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.4 y §8.5: read deja unreadCount en 0 y manda el acuse a Meta (best effort,
/// una sola vez, sólo con conexión Active); resolve/reopen con If-Match (428/412), sus 422, auditoría en
/// la misma transacción; y la versión que sube la ingesta hace chocar un resolve viejo.</summary>
public sealed class ConversationLifecycleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, long Version, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var item = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0];
        return new Fixture(factory, connectionString, tenant, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64(), client);
    }

    [Fact]
    public async Task ReadZeroesTheCounterAndAcknowledgesToMetaOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var first = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");
        var second = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        // Una sola versión nueva: el segundo read encuentra el contador en 0 y no toca nada.
        Assert.Equal($"0|{f.Version + 1}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        var ack = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"status\":\"read\"", ack.Body, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":\"wamid.in\"", ack.Body, StringComparison.Ordinal);
    }

    // §8.4: read no lleva token de concurrencia. Con la ingesta subiendo version por SQL (§7.5) entre dos
    // reads, el segundo sigue siendo 204, deja el contador en 0 y acusa el entrante nuevo.
    [Fact]
    public async Task ReadAfterAnInboundThatBumpedTheVersionIsStill204()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");
        var url = $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read";
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, url)).StatusCode);
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra"));
        await DrainDeliveriesAsync(f.Factory);
        var versionAfterInbound = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var read = await SendAsync(f.Client, HttpMethod.Post, url);

        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal($"0|{versionAfterInbound + 1}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        Assert.Contains(f.Factory.MetaHandler.Requests, request => request.Body?.Contains("\"message_id\":\"wamid.2\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ReadIsStill204WhenMetaFailsOrTheConnectionIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Throw = new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT unread_count::bigint FROM messaging.conversations"));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 2");
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        f.Factory.MetaHandler.Throw = null;
        f.Factory.MetaHandler.Requests.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task ResolveAndReopenFollowIfMatchWithTheirCodesAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);

        var without = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve");
        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: "\"999\"");
        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{f.Version}\"");
        var summary = await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopened = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopenedSummary = await reopened.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var openAgain = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{reopenedSummary.GetProperty("version").GetInt64()}\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(without)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Resolved", summary.GetProperty("status").GetString());
        Assert.Equal(f.Version + 1, summary.GetProperty("version").GetInt64());
        Assert.Equal("Ventas", summary.GetProperty("connectionName").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("messaging.conversation.already_resolved", (await ProblemAsync(again)).Code);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("Open", reopenedSummary.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, openAgain.StatusCode);
        Assert.Equal("messaging.conversation.already_open", (await ProblemAsync(openAgain)).Code);
        Assert.Equal("messaging.conversation.reopened,messaging.conversation.resolved", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(action, ',' ORDER BY action) FROM audit.entries WHERE source = 'messaging' AND resource_type = 'conversation' AND resource_id = @id", ("id", f.ConversationId.ToString())));
    }

    // §7.5: la ingesta sube version; un resolve con la versión vieja es 412.
    [Fact]
    public async Task AnInboundThatArrivesAfterLoadingMakesTheResolveConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra"));
        await DrainDeliveriesAsync(f.Factory);

        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: $"\"{f.Version}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task ReadResolveAndReopenRejectOtherTenantsAndTheReadPermissionAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var readOnly = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(other.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);

        // Decisión 5: con el módulo apagado, los tres son 403 aunque el permiso esté.
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{f.Version}\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{f.Version}\"")).StatusCode);
    }
}
