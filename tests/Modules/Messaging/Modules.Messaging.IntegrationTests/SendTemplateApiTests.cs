using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §5.2 por HTTP y contra la base: una plantilla sale con la ventana cerrada (201), se guarda <c>Template</c> con su
/// texto armado y su detalle, repetir el <c>clientId</c> no vuelve a llamar a Meta, el 422 de claves o de valores, y Meta 131050 que
/// apaga el marketing del cliente.</summary>
public sealed class SendTemplateApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"},{"type":"FOOTER","text":"QCode"}]},
          {"id":"2","name":"promo","language":"es","status":"APPROVED","category":"MARKETING","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Mira"}]}
        ]}
        """;

    private static string TemplateUrl(Fixture f) => $"{MessagesUrl(f.Tenant.TenantId, f.ConversationId)}/template";

    /// <summary>Una conversación con la última entrada de hace 3 días: la ventana de 24 h está cerrada.</summary>
    private static async Task<Fixture> ArrangeAsync(TestDatabase database, Guid? customerId = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedBsuidConversationAsync(
            factory, connectionString, tenant.TenantId, connectionId, "CO.573001234567", "573001234567", DateTimeOffset.UtcNow.AddDays(-3));
        if (customerId is { } customer)
        {
            await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET customer_id = @c", ("c", customer));
        }

        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        return new Fixture(factory, connectionString, tenant, conversationId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static void ScriptSendOk(FakeMetaGraphHandler meta, string wamid = "wamid.tpl") =>
        meta.Respond("/111/messages", HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","messages":[{"id":"{{wamid}}"}]}""");

    private static int SendCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    private static object Body(Guid clientId, string firstName = "Ana", string name = "seguimiento") =>
        new { clientId, name, language = "es", parameters = new Dictionary<string, string> { ["first_name"] = firstName } };

    [Fact]
    public async Task ATemplateGoesWithTheWindowClosedAndIsStoredWithItsRenderedTextAndDetail()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var response = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f), Body(clientId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Template", "Sent", "Outbound"), (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("direction").GetString()));
        Assert.Equal("Hola Ana, ¿sigues interesado?\n\nQCode", message.GetProperty("text").GetString());
        Assert.Equal("seguimiento", message.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("es", message.GetProperty("template").GetProperty("language").GetString());
        Assert.Equal(clientId, message.GetProperty("clientId").GetGuid());

        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(send.Body!);
        Assert.Equal("template", payload.RootElement.GetProperty("type").GetString());
        // Decisión 8: la conversación tiene BSUID y teléfono; la plantilla va por `to`.
        Assert.Equal("573001234567", payload.RootElement.GetProperty("to").GetString());
        Assert.False(payload.RootElement.TryGetProperty("recipient", out _));
        var parameter = payload.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")[0];
        Assert.Equal(("Ana", "first_name"), (parameter.GetProperty("text").GetString(), parameter.GetProperty("parameter_name").GetString()));

        Assert.Equal("11|1|Hola Ana, ¿sigues interesado?\n\nQCode", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || status || '|' || text FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("seguimiento|es|MARKETING|Ana", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->'template'->>'name' || '|' || (details->'template'->>'language') || '|' || (details->'template'->>'category') || '|' || (details->'template'->'parameters'->>'first_name') FROM messaging.messages WHERE direction = 2"));
        // La foto de la conversación, como cualquier saliente: id, tipo, texto como preview y estado.
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT c.last_message_id = m.id AND c.last_message_kind = 11 AND c.last_message_direction = 2 AND c.last_message_status = 1 AND c.last_message_preview = m.text FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.direction = 2"));

        var thread = await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), Ct);
        var stored = thread.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Template");
        Assert.Equal("seguimiento", stored.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, thread.GetProperty("items").EnumerateArray().First(item => item.GetProperty("kind").GetString() != "Template").GetProperty("template").ValueKind);
    }

    [Fact]
    public async Task ARepeatedClientIdAnswersTheSameMessageWithItsTemplateAndMetaSeesOneCall()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var first = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f), Body(clientId));
        var second = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f), Body(clientId, firstName: "Otra"));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
        Assert.Equal("Hola Ana, ¿sigues interesado?\n\nQCode", secondBody.GetProperty("text").GetString());
        Assert.Equal("seguimiento", secondBody.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task TheTextEndpointStillRefusesWithTheWindowClosed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "hola" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.window_closed", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ParameterKeysThatAreNotExactlyTheTemplatesAre422AMismatchWithoutMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f),
            new { clientId = Guid.CreateVersion7(), name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana", ["extra"] = "x" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.parameters_mismatch", (await ProblemAsync(response)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Theory]
    [InlineData("noexiste", "es")]
    [InlineData("seguimiento", "pt_BR")]
    [InlineData("promo", "es")]
    public async Task ATemplateThatIsMissingOrNotSendableIs422NotAvailable(string name, string language)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f),
            new { clientId = Guid.CreateVersion7(), name, language, parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.not_available", (await ProblemAsync(response)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AValueThatBreaksMetasRulesIs422OnItsExactKey()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f), Body(Guid.CreateVersion7(), firstName: "Ana\nMaría"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["parameters.first_name"], keys);
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), ManagePermissions);

        var response = await SendAsync(intruder, HttpMethod.Post, TemplateUrl(f), Body(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnassignedConversationIsTakenBySendingATemplate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);

        await SendAsync(f.Client, HttpMethod.Post, TemplateUrl(f), Body(Guid.CreateVersion7()));

        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
    }

    [Fact]
    public async Task AMarketingOptOutFromMetaIsKeptAsFailedAndMarksTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        var conversationId = await SeedBsuidConversationAsync(factory, connectionString, tenant.TenantId, connectionId, "CO.573001234567", "573001234567", DateTimeOffset.UtcNow.AddDays(-3));
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET customer_id = @c", ("c", customerId));
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131050));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, $"{MessagesUrl(tenant.TenantId, conversationId)}/template", Body(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.message.rejected", (await ProblemAsync(response)).Code);
        Assert.Equal("4|131050", await ScalarAsync<string>(connectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT marketing_opt_out AND marketing_opt_out_source = 'meta' FROM customers.customers WHERE id = @id", ("id", customerId)));
    }
}
