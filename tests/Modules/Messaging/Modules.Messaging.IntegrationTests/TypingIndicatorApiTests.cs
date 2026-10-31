using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary><c>POST …/conversations/{id}/typing</c> por HTTP y contra la base: 204 aunque Meta falle, y la conversación no cambia en
/// nada: ni <c>version</c>, ni <c>updated_at</c>, ni <c>unread_count</c>, ni el asignado. El «escribiendo» sólo existe en Meta.</summary>
public sealed class TypingIndicatorApiTests
{
    private const string StateSql = "SELECT version || '|' || updated_at::text || '|' || unread_count || '|' || coalesce(assigned_member_id::text, '-') FROM messaging.conversations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    /// <summary>El entrante <c>w1</c> deja la conversación abierta, sin asignar y con un no leído.</summary>
    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string[]? permissions = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        using var reader = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await reader.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        factory.MetaHandler.Requests.Clear();
        return new Fixture(factory, connectionString, tenant, conversationId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions ?? ManagePermissions));
    }

    private static string TypingUrl(Guid tenantId, Guid conversationId) => $"{ConversationUrl(tenantId, conversationId)}/typing";

    private static Task<HttpResponseMessage> TypeAsync(Fixture f) => SendAsync(f.Client, HttpMethod.Post, TypingUrl(f.Tenant.TenantId, f.ConversationId));

    [Fact]
    public async Task TheIndicatorGoesToMetaAndTheConversationDoesNotChange()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Client;
        var before = await ScalarAsync<string>(f.ConnectionString, StateSql);
        var events = await ScalarAsync<long>(f.ConnectionString, "SELECT count(*) FROM messaging.messages");

        var response = await TypeAsync(f);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var request = Assert.Single(f.Factory.MetaHandler.Requests);
        Assert.EndsWith("/111/messages", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":\"w1\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"typing_indicator\":{\"type\":\"text\"}", request.Body, StringComparison.Ordinal);
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString, StateSql));
        Assert.Equal(events, await ScalarAsync<long>(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Fact]
    public async Task AFailureFromMetaStillAnswers204()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Client;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.InternalServerError, FakeMetaGraphHandler.GraphError(2));
        var before = await ScalarAsync<string>(f.ConnectionString, StateSql);

        var response = await TypeAsync(f);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(f.Factory.MetaHandler.Requests);
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString, StateSql));
    }

    [Fact]
    public async Task WithoutTheManagePermissionIs403AndNothingGoesToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ReadPermissions);
        using var _ = f.Factory;
        using var __ = f.Client;

        var response = await TypeAsync(f);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task AnUnknownConversationIs404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Client;

        var response = await SendAsync(f.Client, HttpMethod.Post, TypingUrl(f.Tenant.TenantId, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task AResolvedConversationAnswers204WithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Client;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved'");

        var response = await TypeAsync(f);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task InstagramGetsTypingOnAndTheConversationDoesNotChange()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using var anonymous = factory.CreateClient();
        await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        factory.MetaHandler.Requests.Clear();
        var before = await ScalarAsync<string>(connectionString, StateSql);

        var response = await SendAsync(client, HttpMethod.Post, TypingUrl(tenant.TenantId, conversationId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var request = Assert.Single(factory.MetaHandler.Requests);
        Assert.Equal($"/v24.0/{InstagramPayloads.DefaultAccountId}/messages", request.Uri!.AbsolutePath);
        Assert.Contains("\"sender_action\":\"typing_on\"", request.Body, StringComparison.Ordinal);
        Assert.Equal(before, await ScalarAsync<string>(connectionString, StateSql));
    }
}
