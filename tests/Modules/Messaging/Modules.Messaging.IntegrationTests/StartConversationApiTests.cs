using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §5.2 y decisión 9 por HTTP y contra la base: <c>POST /conversations</c> crea (201) o reutiliza (200) la
/// conversación del cliente en la conexión, asignada a quien llama, y envía la plantilla; y la ingesta adopta la fila sólo con teléfono
/// cuando llega el BSUID, sin duplicar.</summary>
public sealed class StartConversationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid CustomerId, HttpClient Client, string Url);

    private const string Catalog = """
        {"data":[{"name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
                  "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]}]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        var sent = 0;
        factory.MetaHandler.RespondWith("/111/messages", _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.start.{{Interlocked.Increment(ref sent)}}"}]}"""));
        return new Fixture(
            factory, connectionString, tenant, connectionId, customerId,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), ConversationsUrl(tenant.TenantId));
    }

    private static object Body(Fixture f, Guid? customerId = null, Guid? clientId = null) => new
    {
        clientId = clientId ?? Guid.CreateVersion7(),
        connectionId = f.ConnectionId,
        customerId = customerId ?? f.CustomerId,
        template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } },
    };

    private static int SendCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    [Fact]
    public async Task ACustomerWithOnlyAPhoneGetsAConversationAssignedToTheCallerAndTheTemplateGoesByPhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var conversation = body.GetProperty("conversation");
        var message = body.GetProperty("message");
        var memberId = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        Assert.Equal(("Open", "Ventas"), (conversation.GetProperty("status").GetString(), conversation.GetProperty("connectionName").GetString()));
        Assert.Equal(memberId, conversation.GetProperty("assignedTo").GetProperty("memberId").GetGuid());
        Assert.True(conversation.GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
        Assert.Equal(f.CustomerId, conversation.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Template", message.GetProperty("kind").GetString());
        Assert.Equal("Hola Ana, ¿sigues interesado?", message.GetProperty("text").GetString());
        Assert.Equal("Hola Ana, ¿sigues interesado?", conversation.GetProperty("lastMessage").GetProperty("preview").GetString());

        // La fila: sin BSUID, con el teléfono en dígitos, atada al cliente y asignada.
        Assert.Equal($"|573001234567|{f.CustomerId}|{memberId}|Open", await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(user_id, '') || '|' || wa_id || '|' || customer_id || '|' || assigned_member_id || '|' || status FROM messaging.conversations"));
        // El evento Taken como en `take`, y la auditoría de la apertura.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'Taken'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.started'"));
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"to\":\"573001234567\"", send.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recipient\"", send.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBsuidThatArrivesLaterIsAdoptedByTheSameConversationWithoutDuplicating()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));
        var created = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "sí, cuéntame"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal($"{created}|{MetaPayloads.UserIdFor("573001234567")}|573001234567|{f.CustomerId}|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT id || '|' || user_id || '|' || wa_id || '|' || customer_id || '|' || unread_count FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    [Fact]
    public async Task AFollowingStartForTheSameCustomerAndConnectionSendsInTheSameConversationWith200()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var first = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));

        var second = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(firstBody.GetProperty("conversation").GetProperty("id").GetGuid(), secondBody.GetProperty("conversation").GetProperty("id").GetGuid());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 11"));
    }

    [Fact]
    public async Task ARepeatedClientIdDoesNotSendTwice()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var clientId = Guid.CreateVersion7();

        await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f, clientId: clientId));
        var again = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f, clientId: clientId));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 11"));
    }

    [Fact]
    public async Task ALegacyPhoneOnlyConversationOfTheCustomerIsReusedNotDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var legacy = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(legacy, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("conversation").GetProperty("id").GetGuid());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Fact]
    public async Task ACustomerWhoWroteFirstWithABsuidIsReusedByCustomerId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "hola"));
        await DrainDeliveriesAsync(f.Factory);
        var existing = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(existing, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("conversation").GetProperty("id").GetGuid());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnUnknownCustomerIs404WithoutCreatingAnything()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, Body(f, customerId: Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.customer.not_found", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    [Fact]
    public async Task ATemplateThatIsNotAvailableCreatesNoConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url,
            new { clientId = Guid.CreateVersion7(), connectionId = f.ConnectionId, customerId = f.CustomerId, template = new { name = "otra", language = "es", parameters = new Dictionary<string, string>() } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.not_available", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AnInvalidValueIs422OnItsKeyAndAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), ManagePermissions);

        var invalid = await SendAsync(f.Client, HttpMethod.Post, f.Url,
            new { clientId = Guid.CreateVersion7(), connectionId = f.ConnectionId, customerId = f.CustomerId, template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "a\tb" } } });
        var forbidden = await SendAsync(intruder, HttpMethod.Post, f.Url, Body(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(["parameters.first_name"], (await ProblemAsync(invalid)).ErrorKeys);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs422AndCreatesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url,
            new { clientId = Guid.CreateVersion7(), connectionId = Guid.CreateVersion7(), customerId = f.CustomerId, template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.connection_unavailable", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }
}
