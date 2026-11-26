using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-17 (abrir la conversación del cliente) por HTTP y contra la base: <c>POST /conversations/open</c> encuentra
/// (200) o crea (201) la conversación del cliente sin enviar nada, abierta y asignada a quien llama si no tenía asesor; y la ingesta
/// adopta después la fila creada sólo con teléfono.</summary>
public sealed class OpenCustomerConversationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid CustomerId, HttpClient Client, string Url);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        return new Fixture(
            factory, connectionString, tenant, connectionId, customerId,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), $"{ConversationsUrl(tenant.TenantId)}/open");
    }

    private static Task<HttpResponseMessage> OpenAsync(Fixture f, object? body = null, HttpClient? client = null) =>
        SendAsync(client ?? f.Client, HttpMethod.Post, f.Url, body ?? new { customerId = f.CustomerId });

    private static async Task<JsonElement> ConversationOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("conversation");

    [Fact]
    public async Task OpensAnEmptyConversationAssignedToTheCallerWith201()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await OpenAsync(f);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("created").GetBoolean());
        var conversation = body.GetProperty("conversation");
        var id = conversation.GetProperty("id").GetGuid();
        Assert.Equal("Open", conversation.GetProperty("status").GetString());
        Assert.True(conversation.GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
        Assert.Equal(f.CustomerId, conversation.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, conversation.GetProperty("lastMessage").ValueKind);

        var inbox = await f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}?status=Open", Ct);
        Assert.Contains(id, inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        // Ningún mensaje: el hilo sólo tiene el evento de asignación.
        var thread = await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, id), Ct);
        Assert.All(thread.GetProperty("items").EnumerateArray(), item => Assert.Equal(JsonValueKind.Object, item.GetProperty("event").ValueKind));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind <> 13"));
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    [Fact]
    public async Task ASecondOpenReturnsTheSameConversationWith200()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var first = await OpenAsync(f);

        var second = await OpenAsync(f);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(body.GetProperty("created").GetBoolean());
        Assert.Equal((await ConversationOf(first)).GetProperty("id").GetGuid(), body.GetProperty("conversation").GetProperty("id").GetGuid());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'Taken'"));
    }

    [Fact]
    public async Task AResolvedConversationComesBackOpen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var opened = await ConversationOf(await OpenAsync(f));
        var url = ConversationUrl(f.Tenant.TenantId, opened.GetProperty("id").GetGuid());
        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{opened.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        var response = await OpenAsync(f);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var conversation = await ConversationOf(response);
        Assert.Equal("Open", conversation.GetProperty("status").GetString());
        Assert.True(conversation.GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'Reopened'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.reopened'"));
    }

    [Fact]
    public async Task SeveralWhatsAppConnectionsAnswer422ConnectionRequired()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "444");

        var response = await OpenAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.connection_required", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));

        // Con el número elegido, abre ahí.
        var chosen = await OpenAsync(f, new { customerId = f.CustomerId, connectionId = f.ConnectionId });
        Assert.Equal(HttpStatusCode.Created, chosen.StatusCode);
        Assert.Equal(f.ConnectionId, (await ConversationOf(chosen)).GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task AnUnreachableCustomerLeavesNoConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET phone_e164 = NULL, whatsapp_user_id = NULL WHERE id = @id", ("id", f.CustomerId));

        var unreachable = await OpenAsync(f);
        var invalid = await OpenAsync(f, new { customerId = (Guid?)null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreachable.StatusCode);
        Assert.Equal("messaging.customer.unreachable", (await ProblemAsync(unreachable)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(["customerId"], (await ProblemAsync(invalid)).ErrorKeys);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Fact]
    public async Task WithoutManagePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await OpenAsync(f, client: reader);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Fact]
    public async Task TheInboundOfThatNumberLandsInTheOpenedConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var created = (await ConversationOf(await OpenAsync(f))).GetProperty("id").GetGuid();

        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "hola"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal($"{created}|{MetaPayloads.UserIdFor("573001234567")}|{f.CustomerId}|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT id || '|' || user_id || '|' || customer_id || '|' || unread_count FROM messaging.conversations"));
    }
}
