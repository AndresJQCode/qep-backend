using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-30 §5.2, §5.3 y §6.3: <c>POST /api/v1/external/whatsapp/messages</c> por HTTP y contra la base real. La idempotencia es lo
/// que más importa acá: un reintento que manda dos veces es la peor falla de la función, así que cada caso cuenta las llamadas a Meta.
/// </summary>
public sealed class ExternalSendApiTests
{
    private const string Phone = "573001234567";
    private const string OtherPhone = "573001234568";
    private const string UnknownPhone = "573001234569";
    private const string TemplateName = "aviso_despacho";
    private const string Language = "es_CO";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu pedido {{2}} va en camino."}]},
          {"id":"2","name":"aviso_general","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Tenemos novedades para ti."}]},
          {"id":"3","name":"aviso_cierre","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Hola {{1}}, cerramos a las {{2}}."}]},
          {"id":"4","name":"aviso_logo","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Tu guía 1014853 está Pendiente."}]},
          {"id":"5","name":"factura_pdf","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Tu factura está lista."}]},
          {"id":"6","name":"qep_cotizacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
           "components":[{"type":"HEADER","format":"DOCUMENT"},
                         {"type":"BODY","text":"Hola {{nombre_contacto}}, tu cotización {{numero_cotizacion}} por {{valor_total}} vence el {{vigencia}}."}]}
        ]}
        """;

    private const string MetaOk =
        """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.external"}]}""";

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId, Guid KeyId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, Func<QepApiFactory, RegisteredTenant, Task<Guid[]?>>? restrictKeyTo = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var allowed = restrictKeyTo is null ? null : await restrictKeyTo(factory, tenant);
        var (keyId, plaintext) = await SeedApiKeyAsync(connectionString, tenant.TenantId, member, SendScope, allowed);
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        return new Fixture(factory, connectionString, tenant, member, connectionId, keyId, CreateExternalClient(factory, plaintext));
    }

    private static void ScriptMetaOk(FakeMetaGraphHandler meta) => meta.Respond("/111/messages$", HttpStatusCode.OK, MetaOk);

    private static Dictionary<string, string> DefaultParameters() => new() { ["1"] = "Ana", ["2"] = "4821" };

    private static object Body(
        Guid idempotencyKey, Guid connectionId, string to = Phone, string name = TemplateName, string language = Language, Dictionary<string, string>? parameters = null) =>
        new { idempotencyKey, connectionId, to, template = new { name, language, parameters = parameters ?? DefaultParameters() } };

    private static object HeaderBody(
        Guid idempotencyKey, Guid connectionId, string name, object? header, string language = Language, Dictionary<string, string>? parameters = null) =>
        new { idempotencyKey, connectionId, to = Phone, template = new { name, language, header, parameters = parameters ?? [] } };

    /// <summary>El cuerpo a mano, con el JSON del encabezado tal cual, para los casos que un objeto anónimo no puede armar.</summary>
    private static string RawHeaderBody(Guid connectionId, string name, string headerJson) =>
        $$$"""{"idempotencyKey":"{{{Guid.CreateVersion7()}}}","connectionId":"{{{connectionId}}}","to":"{{{Phone}}}","template":{"name":"{{{name}}}","language":"{{{Language}}}","parameters":{},"header":{{{headerJson}}}}}""";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body) => client.PostAsJsonAsync(ExternalMessagesUrl, body, Ct);

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string json) =>
        client.PostAsync(ExternalMessagesUrl, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

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

    private static async Task AssertValidationAsync(HttpResponseMessage response, params string[] keys)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(keys, errorKeys);
    }

    /// <summary>Meta retiene la primera llamada de envío hasta que la prueba la suelte, y responde cada llamada según su número. Con esto
    /// el segundo request llega mientras el primero tiene el reclamo abierto.</summary>
    private sealed class HeldMeta(Func<int, HttpResponseMessage> answer)
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpResponseMessage Respond()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                Entered.TrySetResult();
                Assert.True(Released.Task.Wait(TimeSpan.FromSeconds(60)), "La prueba nunca soltó la primera llamada a Meta.");
            }

            return answer(call);
        }
    }

    private static HeldMeta HoldFirstSend(Fixture f, Func<int, HttpResponseMessage> answer)
    {
        var held = new HeldMeta(answer);
        f.Factory.MetaHandler.RespondWith("/111/messages$", _ => held.Respond());
        return held;
    }

    /// <summary>El primer request ya está en Meta; el segundo arranca y la prueba espera a verlo bloqueado en el candado de la fila. Sólo
    /// entonces suelta a Meta: si no, el segundo podría llegar tarde y ejercer el camino sin carrera.</summary>
    private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(Fixture f, HeldMeta held, object firstBody, object secondBody)
    {
        var first = PostAsync(f.Client, firstBody);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = PostAsync(f.Client, secondBody);
        await WaitForABlockedBackendAsync(f.ConnectionString);
        held.Released.TrySetResult();
        return (await first, await second);
    }

    private static async Task WaitForABlockedBackendAsync(string connectionString)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountAsync(connectionString, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("El segundo request nunca quedó esperando el candado de la fila.");
    }

    [Fact]
    public async Task SendsIntoANewOpenUnassignedConversationAndSignsWithTheKey()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, to: "+57 300-123-4567"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        var messageId = body.GetProperty("messageId").GetGuid();
        var conversationId = body.GetProperty("conversationId").GetGuid();

        Assert.Equal($"{conversationId}|Open|-|{Phone}|0", await ScalarAsync<string>(f.ConnectionString,
            "SELECT id || '|' || status || '|' || coalesce(assigned_member_id::text, '-') || '|' || wa_id || '|' || unread_count FROM messaging.conversations"));
        Assert.Equal($"{messageId}|4|{f.KeyId}|ERP Contable|-|1|Hola Ana, tu pedido 4821 va en camino.", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT id || '|' || sent_by_kind || '|' || sent_by_api_key_id || '|' || sent_by_api_key_name || '|' || coalesce(sent_by_member_id::text, '-')
                   || '|' || status || '|' || text
            FROM messaging.messages WHERE direction = 2
            """));
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(send.Body!);
        Assert.Equal(Phone, payload.RootElement.GetProperty("to").GetString());
        Assert.Equal(TemplateName, payload.RootElement.GetProperty("template").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnExistingConversationKeepsItsStatusAndAssignee()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var existing = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", Phone);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', assigned_member_id = @member, assigned_at = now(), unread_count = 2", ("member", f.MemberId));

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(existing, (await BodyAsync(response)).GetProperty("conversationId").GetGuid());
        Assert.Equal(1L, await ConversationsAsync(f));
        Assert.Equal($"Resolved|{f.MemberId}|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || assigned_member_id || '|' || unread_count FROM messaging.conversations"));
    }

    [Fact]
    public async Task ACustomerIsLinkedOnlyWithASingleMatch()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        // Un wamid por envío: la columna es única.
        var sends = 0;
        f.Factory.MetaHandler.RespondWith("/111/messages$", _ => FakeMetaGraphHandler.Json(
            HttpStatusCode.OK, $$"""{"messages":[{"id":"wamid.{{Interlocked.Increment(ref sends)}}"}]}"""));
        var customers = await CreateCustomersAsync(f.Factory, f.Tenant, ("Una", "300 123 4567"), ("Dos A", "300 123 4568"), ("Dos B", "300 123 4568"));

        foreach (var phone in new[] { Phone, OtherPhone, UnknownPhone })
        {
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, to: phone))).StatusCode);
        }

        Assert.Equal($"{customers[0]}|Una", await ScalarAsync<string>(f.ConnectionString,
            "SELECT customer_id || '|' || profile_name FROM messaging.conversations WHERE wa_id = @phone", ("phone", Phone)));
        Assert.Equal(2L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.conversations WHERE customer_id IS NULL AND profile_name IS NULL AND wa_id = ANY(@phones)", ("phones", new[] { OtherPhone, UnknownPhone })));
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    [Fact]
    public async Task ATemplateProblemLeavesNoEmptyThread()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var unknown = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, name: "no_existe"));
        var mismatch = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, parameters: new() { ["1"] = "Ana" }));

        await AssertProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "messaging.template.not_available");
        await AssertProblemAsync(mismatch, HttpStatusCode.UnprocessableEntity, "messaging.template.parameters_mismatch");
        Assert.Equal(0L, await ConversationsAsync(f));
        Assert.Equal(0, SendCalls(f));
    }

    [Fact]
    public async Task AnUnusableConnectionIs422ConnectionUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "messaging.connection_unavailable");
        Assert.Equal(0L, await ConversationsAsync(f));
        Assert.Equal(0, SendCalls(f));
    }

    [Fact]
    public async Task ANumberOutsideTheKeyIs403()
    {
        await using var database = await StartDatabaseAsync();
        Guid other = default;
        var f = await ArrangeAsync(database, async (factory, tenant) =>
        {
            other = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Soporte", "333", "444");
            return [other];
        });
        using var host = f.Factory;

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "external.connection_not_allowed");
        Assert.Equal(0L, await ConversationsAsync(f));
    }

    [Fact]
    public async Task AnInstagramConnectionIs422ChannelUnsupported()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var instagram = await SeedInstagramConnectionAsync(f.Factory, f.Tenant.TenantId, "Instagram", "17841400000000000");

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), instagram));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "messaging.channel_unsupported");
    }

    [Fact]
    public async Task MissingFieldsAre422WithTheirKeys()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await AssertValidationAsync(
            await PostRawAsync(f.Client, "{}"), "connectionId", "idempotencyKey", "template.language", "template.name", "to");
    }

    [Fact]
    public async Task ANonUuidIdempotencyKeyIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, new
        {
            idempotencyKey = "pedido-4821",
            connectionId = f.ConnectionId,
            to = Phone,
            template = new { name = TemplateName, language = Language, parameters = DefaultParameters() },
        });

        await AssertValidationAsync(response, "idempotencyKey");
    }

    [Fact]
    public async Task AValidationFailureReadsAsPlainSpanishAndKeepsItsContract()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, new
        {
            idempotencyKey = "pedido-4821",
            connectionId = f.ConnectionId,
            to = Phone,
            template = new { name = TemplateName, language = Language, parameters = DefaultParameters() },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("Datos no válidos", body.GetProperty("title").GetString());
        Assert.Equal("Algunos datos no son válidos. Revisa cada campo e intenta de nuevo.", body.GetProperty("detail").GetString());
        Assert.Equal("validation.failed", body.GetProperty("code").GetString());
        Assert.Equal(422, body.GetProperty("status").GetInt32());
        Assert.Equal("""{"idempotencyKey":["Envía un UUID."]}""", body.GetProperty("errors").GetRawText());
    }

    [Theory]
    [InlineData("3001234567")] // Sin indicativo se leería como +30 y le llegaría a otra persona.
    [InlineData("0573001234567")]
    public async Task ANumberWithoutCountryCodeIs422OnTo(string to)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await AssertValidationAsync(await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, to: to)), "to");
    }

    [Fact]
    public async Task ABadParameterValueIs422WithItsKey()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId, parameters: new() { ["1"] = "Ana\nMaría", ["2"] = "4821" }));

        await AssertValidationAsync(response, "template.parameters.1");
    }

    [Fact]
    public async Task ABrokenBodyIs422WithCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await AssertValidationAsync(await PostRawAsync(f.Client, "{no json"), "body");
    }

    [Fact]
    public async Task ANumericParameterIs422WithCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var json = $$"""
            {"idempotencyKey":"{{Guid.CreateVersion7()}}","connectionId":"{{f.ConnectionId}}","to":"{{Phone}}",
             "template":{"name":"{{TemplateName}}","language":"{{Language}}","parameters":{"1":"Ana","2":4821} } }
            """;

        await AssertValidationAsync(await PostRawAsync(f.Client, json), "body");
    }

    [Fact]
    public async Task ATemplateWithoutVariablesNeedsNoParameters()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var idempotencyKey = Guid.CreateVersion7();
        var body = new { idempotencyKey, connectionId = f.ConnectionId, to = Phone, template = new { name = "aviso_general", language = Language } };

        var first = await PostAsync(f.Client, body);
        var repeat = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task RepeatingTheSameRequestAnswers200WithoutCallingMetaAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        var first = await PostAsync(f.Client, body);
        var repeat = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var (original, repeated) = (await BodyAsync(first), await BodyAsync(repeat));
        Assert.Equal(original.GetProperty("messageId").GetGuid(), repeated.GetProperty("messageId").GetGuid());
        Assert.Equal(original.GetProperty("conversationId").GetGuid(), repeated.GetProperty("conversationId").GetGuid());
        Assert.Equal("Sent", repeated.GetProperty("status").GetString());
        Assert.Equal(1, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task ReplayWinsOverEverythingThatChangedSince()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, body)).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @member, blocked_wa_id = wa_id", ("member", f.MemberId));
        await ExecuteAsync(f.ConnectionString, "DELETE FROM messaging.message_templates");
        // El estado actual del mensaje, no una foto del primer envío.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.messages SET status = 3 WHERE direction = 2");

        var repeat = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("Read", (await BodyAsync(repeat)).GetProperty("status").GetString());
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task ARotatedKeyReplaysToo()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, body)).StatusCode);
        var (_, rotated) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, name: "ERP rotada");
        using var rotatedClient = CreateExternalClient(f.Factory, rotated);

        var repeat = await PostAsync(rotatedClient, body);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(1, SendCalls(f));
        Assert.Equal("ERP Contable", await ScalarAsync<string>(f.ConnectionString, "SELECT sent_by_api_key_name FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AnotherTenantCanUseTheSameIdempotencyKey()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        f.Factory.MetaHandler.Respond("/333/messages$", HttpStatusCode.OK, MetaOk);
        f.Factory.MetaHandler.Respond("/444/message_templates", HttpStatusCode.OK, Catalog);
        var otherTenant = await RegisterTenantAsync(f.Factory);
        await EnableDeveloperAsync(f.ConnectionString, otherTenant.TenantId);
        await EnableMessagingAsync(f.ConnectionString, otherTenant.TenantId);
        var otherConnection = await SeedWhatsAppConnectionAsync(f.Factory, otherTenant.TenantId, "Ventas", "333", "444");
        var otherMember = await OwnerMembershipIdAsync(f.ConnectionString, otherTenant);
        var (_, otherKey) = await SeedApiKeyAsync(f.ConnectionString, otherTenant.TenantId, otherMember, SendScope);
        using var otherClient = CreateExternalClient(f.Factory, otherKey);
        var idempotencyKey = Guid.CreateVersion7();

        var first = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId));
        var second = await PostAsync(otherClient, Body(idempotencyKey, otherConnection));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task TheSameIdempotencyKeyWithAnotherNumberIs409AndOpensNoThread()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var idempotencyKey = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId))).StatusCode);

        var response = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId, to: OtherPhone));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1L, await ConversationsAsync(f));
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task TheSameIdempotencyKeyWithAnotherTemplateIs409()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var idempotencyKey = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId))).StatusCode);

        var response = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId, name: "aviso_cierre"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task TheSameIdempotencyKeyWithOtherParametersIs409()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var idempotencyKey = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId))).StatusCode);

        var response = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId, parameters: new() { ["1"] = "ANA", ["2"] = "4821" }));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AParallelRequestWithAnotherNumberProducesOneSend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var held = HoldFirstSend(f, _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, MetaOk));
        var idempotencyKey = Guid.CreateVersion7();

        var (first, second) = await RaceAsync(f, held, Body(idempotencyKey, f.ConnectionId), Body(idempotencyKey, f.ConnectionId, to: OtherPhone));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task AParallelRepeatProducesOneSend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var held = HoldFirstSend(f, _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, MetaOk));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        var (first, second) = await RaceAsync(f, held, body, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await BodyAsync(first)).GetProperty("messageId").GetGuid(), (await BodyAsync(second)).GetProperty("messageId").GetGuid());
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AParallelRepeatOfAnUnconfirmedSendDoesNotResend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var held = HoldFirstSend(f, _ => FakeMetaGraphHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        var (first, second) = await RaceAsync(f, held, body, body);

        await AssertProblemAsync(first, HttpStatusCode.BadGateway, "external.send_unconfirmed");
        await AssertProblemAsync(second, HttpStatusCode.BadGateway, "external.send_unconfirmed");
        Assert.Equal(1, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task AParallelRepeatOfACertainRejectionResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var held = HoldFirstSend(f, call => call == 1
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, MetaOk));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        var (first, second) = await RaceAsync(f, held, body, body);

        await AssertProblemAsync(first, HttpStatusCode.UnprocessableEntity, "external.message_rejected");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, SendCalls(f));
        Assert.Equal("1", (await ScalarAsync<short>(f.ConnectionString, "SELECT status FROM messaging.messages WHERE direction = 2")).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AnIdempotencyKeyOfAnAdvisorMessageIs409NotAReplay()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var conversation = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", Phone);
        var advisorMessage = await SeedOutboundAsync(f.ConnectionString, conversation, f.Tenant.TenantId, f.ConnectionId, "wamid.advisor");
        var idempotencyKey = await ScalarAsync<Guid>(f.ConnectionString, "SELECT client_id FROM messaging.messages WHERE id = @id", ("id", advisorMessage));

        var response = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task ACertainRejectionIs422AndTheSameIdempotencyKeyResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.UnprocessableEntity, "external.message_rejected");
        var failedId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 2");
        f.Factory.MetaHandler.Reset();
        ScriptMetaOk(f.Factory.MetaHandler);

        var retry = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(failedId, (await BodyAsync(retry)).GetProperty("messageId").GetGuid());
        Assert.Equal(2, SendCalls(f));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }

    [Fact]
    public async Task AnUnconfirmedSendIs502AndARepeatNeverCallsMetaAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.InternalServerError, "{}");
        var idempotencyKey = Guid.CreateVersion7();
        var body = Body(idempotencyKey, f.ConnectionId);

        await AssertProblemAsync(await PostAsync(f.Client, body), HttpStatusCode.BadGateway, "external.send_unconfirmed");
        var repeat = await PostAsync(f.Client, body);
        var otherTemplate = await PostAsync(f.Client, Body(idempotencyKey, f.ConnectionId, name: "aviso_cierre"));

        await AssertProblemAsync(repeat, HttpStatusCode.BadGateway, "external.send_unconfirmed");
        await AssertProblemAsync(otherTemplate, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AMetaLimitIs429WithRetryAfterAndTheSameIdempotencyKeyResends()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(130429));
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        var limited = await PostAsync(f.Client, body);

        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "external.rate_limited");
        Assert.Equal("60", Assert.Single(limited.Headers.GetValues("Retry-After")));
        f.Factory.MetaHandler.Reset();
        ScriptMetaOk(f.Factory.MetaHandler);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, body)).StatusCode);
        Assert.Equal(2, SendCalls(f));
    }

    [Fact]
    public async Task AnOptOutFromMetaIs422OptedOutAndMarksTheLinkedCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var customer = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567");
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131050));

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "external.recipient_opted_out");
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT marketing_opt_out FROM customers.customers WHERE id = @id", ("id", customer)));
    }

    public static TheoryData<string, string> BadHeaders => new()
    {
        { """{"type":"gif","link":"https://cdn.example.com/a.gif"}""", "template.header.type" },
        { """{"type":"image","link":"http://cdn.example.com/logo.png"}""", "template.header.link" },
        { """{"type":"image","link":"/logo.png"}""", "template.header.link" },
        { """{"type":"image","link":"https://user:pass@cdn.example.com/logo.png"}""", "template.header.link" },
        { $$"""{"type":"image","link":"https://cdn.example.com/{{new string('a', 2025)}}"}""", "template.header.link" },
        { """{"type":"image","link":"https://cdn.example.com/logo.png","filename":"logo.png"}""", "template.header.filename" },
        { """{"type":"document","link":"https://cdn.example.com/f.pdf","filename":"a/b.pdf"}""", "template.header.filename" },
        { $$"""{"type":"document","link":"https://cdn.example.com/f.pdf","filename":"{{new string('a', 241)}}"}""", "template.header.filename" },
    };

    [Theory]
    [MemberData(nameof(BadHeaders))]
    public async Task ABadHeaderIs422WithItsKey(string header, string key)
    {
        using (var parsed = JsonDocument.Parse(header))
        {
            // El límite es explícito: 24 del prefijo https://cdn.example.com/ más 2025.
            if (parsed.RootElement.GetProperty("link").GetString() is { Length: > 2000 } link)
            {
                Assert.Equal(2049, link.Length);
            }
        }

        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostRawAsync(f.Client, RawHeaderBody(f.ConnectionId, "aviso_logo", header));

        await AssertValidationAsync(response, key);
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await ConversationsAsync(f));
    }

    private const string LogoLink = "https://cdn.example.com/logo.png";

    private static JsonElement TemplateOfLastSend(Fixture f)
    {
        var send = f.Factory.MetaHandler.Requests.Last(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        return JsonDocument.Parse(send.Body!).RootElement.GetProperty("template").Clone();
    }

    [Fact]
    public async Task AHeaderTypeIsReadIgnoringCase()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostAsync(f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "aviso_logo", new { type = "IMAGE", link = LogoLink }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ASendWithAnImageHeaderReachesMetaWithTheLink()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostAsync(f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "aviso_logo", new { type = "image", link = LogoLink }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var components = TemplateOfLastSend(f).GetProperty("components");
        var expected = JsonNode.Parse($$$"""{"type":"header","parameters":[{"type":"image","image":{"link":"{{{LogoLink}}}"}}]}""");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(components[0].GetRawText())), components[0].GetRawText());
        // Sin variables en el cuerpo no hay componente body: Meta toma el texto de la plantilla.
        Assert.Equal(1, components.GetArrayLength());
        Assert.Equal($"image|{LogoLink}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->'template'->'header'->>'type' || '|' || (details->'template'->'header'->>'link') FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("Tu guía 1014853 está Pendiente.", await ScalarAsync<string>(f.ConnectionString, "SELECT text FROM messaging.messages WHERE direction = 2"));
        // QEP entrega el enlace tal cual: nunca lo descarga.
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, request => request.Uri!.Host.Contains("cdn.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentHeaderTravelsWithItsFilename()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostAsync(
            f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "factura_pdf", new { type = "document", link = "https://cdn.example.com/f.pdf", filename = "Factura-1.pdf" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = TemplateOfLastSend(f).GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("document");
        Assert.Equal("https://cdn.example.com/f.pdf", document.GetProperty("link").GetString());
        Assert.Equal("Factura-1.pdf", document.GetProperty("filename").GetString());
    }

    [Fact]
    public async Task TheQuotationSystemTemplateIsSentWithALink()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var parameters = new Dictionary<string, string>
        {
            ["nombre_contacto"] = "Ana", ["numero_cotizacion"] = "COT-12", ["valor_total"] = "$ 1.500.000", ["vigencia"] = "30/11/2026",
        };

        var response = await PostAsync(
            f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "qep_cotizacion", new { type = "document", link = "https://cdn.example.com/c.pdf" }, language: "es", parameters: parameters));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = TemplateOfLastSend(f).GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("document");
        Assert.Equal("https://cdn.example.com/c.pdf", document.GetProperty("link").GetString());
    }

    [Theory]
    [InlineData("aviso_logo", null, "La plantilla requiere un encabezado de imagen.")]
    [InlineData("aviso_logo", "video", "La plantilla requiere un encabezado de imagen.")]
    [InlineData("aviso_despacho", "image", "La plantilla no tiene encabezado multimedia.")]
    public async Task AHeaderProblemIs422HeaderMismatchAndLeavesNoThread(string name, string? type, string detail)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        object? header = type is null ? null : new { type, link = "https://cdn.example.com/x" };

        var response = await PostAsync(f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, name, header, parameters: name == "aviso_despacho" ? DefaultParameters() : null));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "messaging.template.header_mismatch");
        Assert.Equal(detail, (await BodyAsync(response)).GetProperty("detail").GetString());
        Assert.Equal(0, SendCalls(f));
        Assert.Equal(0L, await ConversationsAsync(f));
    }

    [Fact]
    public async Task AnImageTemplateWithoutParametersMatchStillSaysParametersMismatch()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);

        var response = await PostAsync(
            f.Client, HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "aviso_logo", new { type = "image", link = LogoLink }, parameters: new() { ["1"] = "x" }));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "messaging.template.parameters_mismatch");
    }

    [Fact]
    public async Task RepeatingAHeaderSendAnswers200WithoutCallingMetaAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var body = HeaderBody(Guid.CreateVersion7(), f.ConnectionId, "aviso_logo", new { type = "image", link = LogoLink });

        var first = await PostAsync(f.Client, body);
        var second = await PostAsync(f.Client, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, SendCalls(f));
    }

    public static TheoryData<string> OtherHeaders => new()
    {
        """{"type":"image","link":"https://cdn.example.com/otro.png"}""",
        "null",
    };

    [Theory]
    [MemberData(nameof(OtherHeaders))]
    public async Task TheSameIdempotencyKeyWithAnotherHeaderIs409(string otherHeader)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var key = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "aviso_logo", new { type = "image", link = LogoLink }))).StatusCode);
        var raw = $$$"""{"idempotencyKey":"{{{key}}}","connectionId":"{{{f.ConnectionId}}}","to":"{{{Phone}}}","template":{"name":"aviso_logo","language":"es_CO","parameters":{},"header":{{{otherHeader}}}}}""";

        var response = await PostRawAsync(f.Client, raw);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task TheSameIdempotencyKeyWithAnotherFilenameIs409()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var key = Guid.CreateVersion7();
        const string link = "https://cdn.example.com/f.pdf";
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "factura_pdf", new { type = "document", link, filename = "Factura-1.pdf" }))).StatusCode);

        var response = await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "factura_pdf", new { type = "document", link, filename = "Factura-2.pdf" }));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task TheSameIdempotencyKeyAddingAHeaderToAPlainSendIs409()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        ScriptMetaOk(f.Factory.MetaHandler);
        var key = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(f.Client, Body(key, f.ConnectionId))).StatusCode);

        // El reintento se compara antes del catálogo: aunque aviso_despacho no admita encabezado, la respuesta es 409 y no header_mismatch.
        var response = await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "aviso_despacho", new { type = "image", link = LogoLink }, parameters: DefaultParameters()));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "external.idempotency_key_reused");
        Assert.Equal(1, SendCalls(f));
    }

    [Fact]
    public async Task AFailedHeaderSendResendsWithTheNewLink()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));
        var key = Guid.CreateVersion7();
        const string linkA = "https://cdn.example.com/a.png";
        const string linkB = "https://cdn.example.com/b.png";

        await AssertProblemAsync(
            await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "aviso_logo", new { type = "image", link = linkA })),
            HttpStatusCode.UnprocessableEntity, "external.message_rejected");
        var failedId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 2");
        f.Factory.MetaHandler.Reset();
        ScriptMetaOk(f.Factory.MetaHandler);

        var retry = await PostAsync(f.Client, HeaderBody(key, f.ConnectionId, "aviso_logo", new { type = "image", link = linkB }));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(failedId, (await BodyAsync(retry)).GetProperty("messageId").GetGuid());
        Assert.Equal(linkB, TemplateOfLastSend(f).GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("image").GetProperty("link").GetString());
        Assert.Equal(linkB, await ScalarAsync<string>(f.ConnectionString, "SELECT details->'template'->'header'->>'link' FROM messaging.messages WHERE direction = 2"));
        Assert.Equal(1L, await OutboundMessagesAsync(f));
    }
}
