using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-30 §6.3.1 y §6.3.2 por HTTP y contra la base real: quién puede recibir qué categoría, y qué responde la API según lo que
/// contesta Meta. Cada caso cuenta las llamadas a Meta: un envío que sale cuando no debía, o que sale dos veces, es la falla que importa.
/// </summary>
public sealed class ExternalSendRulesApiTests
{
    private const string Phone = "573001234567";
    private const string OtherPhone = "573001234568";
    private const string Utility = "aviso_despacho";
    private const string Marketing = "promo_mes";
    private const string Language = "es_CO";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Tu pedido va en camino."}]},
          {"id":"2","name":"promo_mes","language":"es_CO","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Este mes tenemos descuentos para ti."}]}
        ]}
        """;

    private const string MetaOk =
        """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.external"}]}""";

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (_, plaintext) = await SeedApiKeyAsync(connectionString, tenant.TenantId, member, SendScope, null);
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        return new Fixture(factory, connectionString, tenant, member, connectionId, CreateExternalClient(factory, plaintext));
    }

    /// <summary>Un wamid distinto por envío: la columna es única.</summary>
    private static void ScriptMetaOk(FakeMetaGraphHandler meta)
    {
        var sends = 0;
        meta.RespondWith("/111/messages$", _ => FakeMetaGraphHandler.Json(
            HttpStatusCode.OK, $$"""{"messages":[{"id":"wamid.{{Interlocked.Increment(ref sends)}}"}]}"""));
    }

    private static object Body(Guid idempotencyKey, Guid connectionId, string template, string to = Phone) =>
        new { idempotencyKey, connectionId, to, template = new { name = template, language = Language } };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body) => client.PostAsJsonAsync(ExternalMessagesUrl, body, Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static int SendCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && !IsReadReceipt(request));

    private static Task<long> ConversationsAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations");

    private static Task<long> OutboundMessagesAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2");

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await ProblemAsync(response)).Code);
    }

    private static async Task BlockAsync(Fixture f) => await ExecuteAsync(
        f.ConnectionString, "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = wa_id", ("member", f.MemberId));

    private static async Task OptOutAsync(Fixture f, Guid customer) =>
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customer));

    // ---- §6.3.1 ----

    [Theory]
    [InlineData(Utility)]
    [InlineData(Marketing)]
    public async Task ABlockedConversationIs422ForEveryCategory(string template)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", Phone);
        await BlockAsync(f);

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, template));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "external.recipient_blocked");
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task AnInactiveCustomerOnlyStopsMarketing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customer = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567");
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET is_active = false WHERE id = @id", ("id", customer));

        var marketing = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));
        var utility = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        await AssertProblemAsync(marketing, HttpStatusCode.UnprocessableEntity, "external.recipient_inactive");
        Assert.Equal(HttpStatusCode.Created, utility.StatusCode);
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AnOptedOutNumberOnlyStopsMarketing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        await OptOutAsync(f, await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567"));

        var marketing = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));
        var utility = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        await AssertProblemAsync(marketing, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.Equal(HttpStatusCode.Created, utility.StatusCode);
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task ANumberWithoutCustomerGoesOutInBothCategories()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var marketing = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));
        var utility = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        Assert.Equal(HttpStatusCode.Created, marketing.StatusCode);
        Assert.Equal(HttpStatusCode.Created, utility.StatusCode);
        Assert.Equal(2, SendCalls(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    [Fact]
    public async Task SeveralMatchesWithAnOptOutStopMarketing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customers = await CreateCustomersAsync(f.Factory, f.Tenant, ("Dos A", "300 123 4567"), ("Dos B", "300 123 4567"));
        await OptOutAsync(f, customers[1]);

        var marketing = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));
        Assert.Equal(0L, await ConversationsAsync(f));
        var utility = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        await AssertProblemAsync(marketing, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.Equal(HttpStatusCode.Created, utility.StatusCode);
        Assert.Equal(1, SendCalls(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE customer_id IS NULL AND wa_id = @phone", ("phone", Phone)));
    }

    /// <summary>Review Focus 4: el cliente ligado a la conversación puede tener otro teléfono que <c>to</c>; igual cuenta.</summary>
    [Fact]
    public async Task ALinkedCustomerWithAnotherPhoneStillStopsMarketing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customer = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4568");
        await OptOutAsync(f, customer);
        await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", Phone);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @id", ("id", customer));

        var marketing = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));
        var utility = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        await AssertProblemAsync(marketing, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.Equal(HttpStatusCode.Created, utility.StatusCode);
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task ANonMarketingOptedOutSendStaysInTheInbox()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customer = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567");
        await OptOutAsync(f, customer);

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"Open|-|{customer}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || coalesce(assigned_member_id::text, '-') || '|' || customer_id FROM messaging.conversations"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT marketing_opt_out FROM customers.customers WHERE id = @id", ("id", customer)));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    // ---- §6.3.2 ----

    [Fact]
    public async Task ACertainRejectionIs422AndTheSameIdempotencyKeyResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132001));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.UnprocessableEntity, "external.message_rejected");
        f.Factory.MetaHandler.Reset();
        ScriptMetaOk(f.Factory.MetaHandler);
        var retry = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(2, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task AnUnconfirmedSendIs502AndNeverResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.InternalServerError, "{}");
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");
        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");

        Assert.Equal(1, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task ATimeoutIsAlso502()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        // La plantilla ya viene del catálogo cacheado cuando se llega al envío; sólo /111/messages se cae.
        f.Factory.MetaHandler.RespondWith("/111/messages$", _ => throw new TaskCanceledException("timeout"));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");
        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");

        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AMetaWithoutWamidIsAlso502()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[]}""");
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");
        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");

        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AnUnconfirmedRowCorrectedByTheStatusWebhookReplays200()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.InternalServerError, "{}");
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);
        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");
        var messageId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 2");

        // Meta sí lo había aceptado: el estado llega por webhook, con el callback que puso el envío.
        using var webhookClient = f.Factory.CreateClient();
        await PostWebhookAsync(webhookClient, MetaPayloads.Status("111", "wamid.late", "delivered", 1760000200, $"qep:{messageId:D}"));
        await DrainDeliveriesAsync(f.Factory);
        var repeat = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var replay = await BodyAsync(repeat);
        Assert.Equal(messageId, replay.GetProperty("messageId").GetGuid());
        Assert.Equal("Delivered", replay.GetProperty("status").GetString());
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task MetaRateLimitIs429WithRetryAfterAndResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(130429));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId, Utility);

        var limited = await PostAsync(f.Client, body);

        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "external.rate_limited");
        Assert.Equal("60", Assert.Single(limited.Headers.GetValues("Retry-After")));
        f.Factory.MetaHandler.Reset();
        ScriptMetaOk(f.Factory.MetaHandler);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, body)).StatusCode);
        Assert.Equal(2, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task MetaOptOutIs422AndMarksTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var customer = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567");
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131050));

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT marketing_opt_out FROM customers.customers WHERE id = @id", ("id", customer)));
    }

    [Fact]
    public async Task MetaOptOutWithoutCustomerMarksNobody()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        // Un cliente de otro número no se toca.
        await CreateCustomerAsync(f.Factory, f.Tenant, "Otra persona", "300 123 4568");
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131050));

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Marketing));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE marketing_opt_out"));
    }

    [Fact]
    public async Task RejectedCredentialsAre422AndLeaveNoRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.Unauthorized, FakeMetaGraphHandler.GraphError(190));

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, Utility));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "messaging.connection_unavailable");
        Assert.Equal("NeedsAttention", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM integrations.connections"));
        Assert.Equal(0L, await OutboundMessagesAsync(f));
    }
}
