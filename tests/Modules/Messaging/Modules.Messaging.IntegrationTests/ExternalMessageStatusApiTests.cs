using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-11-01 parte 2 por HTTP y contra la base real: <c>GET /external/whatsapp/messages/{id}</c> dice el estado real del mensaje
/// (el webhook de Meta lo cambia después del 201) y nunca confirma que exista uno que la clave no puede ver.
/// </summary>
public sealed class ExternalMessageStatusApiTests
{
    private const string Phone = "573001234567";
    private const string Template = "aviso_despacho";
    private const string Language = "es_CO";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Tu pedido va en camino."}]}
        ]}
        """;

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId, HttpClient Client);

    /// <summary><paramref name="metaAccepts"/> va acá porque <see cref="FakeMetaGraphHandler"/> se queda con la primera regla que coincide:
    /// una segunda regla sobre la misma ruta nunca se aplica.</summary>
    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool metaAccepts = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (_, plaintext) = await SeedApiKeyAsync(connectionString, tenant.TenantId, member, SendScope);
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        if (metaAccepts)
        {
            ScriptMetaOk(factory.MetaHandler, "111");
        }
        else
        {
            factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.InternalServerError, "{}");
        }

        return new Fixture(factory, connectionString, tenant, member, connectionId, CreateExternalClient(factory, plaintext));
    }

    /// <summary>Un wamid distinto por envío: la columna es única.</summary>
    private static void ScriptMetaOk(FakeMetaGraphHandler meta, string phoneNumberId)
    {
        var sends = 0;
        meta.RespondWith($"/{phoneNumberId}/messages$", _ => FakeMetaGraphHandler.Json(
            HttpStatusCode.OK, $$"""{"messages":[{"id":"wamid.{{phoneNumberId}}.{{Interlocked.Increment(ref sends)}}"}]}"""));
    }

    private static object Body(Guid connectionId) => new
    {
        idempotencyKey = Guid.CreateVersion7(),
        connectionId,
        to = Phone,
        template = new { name = Template, language = Language },
    };

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string MessageUrl(Guid messageId) => $"{ExternalMessagesUrl}/{messageId}";

    private static async Task<Guid> SendAsync(HttpClient client, Guid connectionId)
    {
        var response = await client.PostAsJsonAsync(ExternalMessagesUrl, Body(connectionId), Ct);
        return (await BodyAsync(response)).GetProperty("messageId").GetGuid();
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("external.message_not_found", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnAcceptedSendReadsSentThenFailedAfterTheWebhook()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var messageId = await SendAsync(f.Client, f.ConnectionId);

        var accepted = await BodyAsync(await f.Client.GetAsync(MessageUrl(messageId), Ct));
        using var webhookClient = f.Factory.CreateClient();
        await PostWebhookAsync(webhookClient, MetaPayloads.Status("111", "wamid.111.1", "failed", 1760000200, $"qep:{messageId:D}", errorCode: 131042));
        await DrainDeliveriesAsync(f.Factory);
        var failed = await BodyAsync(await f.Client.GetAsync(MessageUrl(messageId), Ct));

        Assert.Equal("Sent", accepted.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("failureCode").ValueKind);
        Assert.Equal(messageId, failed.GetProperty("messageId").GetGuid());
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(131042, failed.GetProperty("failureCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("failureReason").GetString()));
    }

    [Fact]
    public async Task AnUnconfirmedSendReadsUnconfirmed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, metaAccepts: false);
        using var host = f.Factory;
        await f.Client.PostAsJsonAsync(ExternalMessagesUrl, Body(f.ConnectionId), Ct);
        var messageId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 2");

        var read = await BodyAsync(await f.Client.GetAsync(MessageUrl(messageId), Ct));

        Assert.Equal("Unconfirmed", read.GetProperty("status").GetString());
        Assert.Equal(-1, read.GetProperty("failureCode").GetInt32());
        Assert.DoesNotContain("Intenta de nuevo", read.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageOfAnotherTenantIsA404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var tenantB = await RegisterTenantAsync(f.Factory);
        await EnableDeveloperAsync(f.ConnectionString, tenantB.TenantId);
        await EnableMessagingAsync(f.ConnectionString, tenantB.TenantId);
        var connectionB = await SeedWhatsAppConnectionAsync(f.Factory, tenantB.TenantId, "Ajena", "333", "444");
        f.Factory.MetaHandler.Respond("/444/message_templates", HttpStatusCode.OK, Catalog);
        ScriptMetaOk(f.Factory.MetaHandler, "333");
        var memberB = await OwnerMembershipIdAsync(f.ConnectionString, tenantB);
        var (_, plaintextB) = await SeedApiKeyAsync(f.ConnectionString, tenantB.TenantId, memberB, SendScope);
        using var clientB = CreateExternalClient(f.Factory, plaintextB);
        var messageOfB = await SendAsync(clientB, connectionB);

        await AssertNotFoundAsync(await f.Client.GetAsync(MessageUrl(messageOfB), Ct));
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync(MessageUrl(messageOfB), Ct)).StatusCode);
    }

    [Fact]
    public async Task AMessageOnAConnectionTheKeyCannotUseIsA404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var other = await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "444");
        f.Factory.MetaHandler.Respond("/444/message_templates", HttpStatusCode.OK, Catalog);
        ScriptMetaOk(f.Factory.MetaHandler, "333");
        var (_, plaintextOther) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, [other], name: "Soporte");
        using var otherClient = CreateExternalClient(f.Factory, plaintextOther);
        var messageOnOther = await SendAsync(otherClient, other);
        var (_, plaintextVentas) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, [f.ConnectionId], name: "Ventas");
        using var ventasClient = CreateExternalClient(f.Factory, plaintextVentas);

        await AssertNotFoundAsync(await ventasClient.GetAsync(MessageUrl(messageOnOther), Ct));
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync(MessageUrl(messageOnOther), Ct)).StatusCode);
    }

    [Fact]
    public async Task AMessageSentByAMemberIsA404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var messageId = await SendAsync(f.Client, f.ConnectionId);
        // Lo mismo que una fila de la bandeja: el autor es un miembro y no queda rastro de la clave (CK de autoría).
        await ExecuteAsync(
            f.ConnectionString,
            "UPDATE messaging.messages SET sent_by_kind = 1, sent_by_member_id = @member, sent_by_api_key_id = NULL, sent_by_api_key_name = NULL WHERE id = @id",
            ("member", f.MemberId), ("id", messageId));

        await AssertNotFoundAsync(await f.Client.GetAsync(MessageUrl(messageId), Ct));
    }

    [Fact]
    public async Task AKeyWithoutTheScopeIsA403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var messageId = await SendAsync(f.Client, f.ConnectionId);
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, ["other.scope"], name: "Sin scope");
        using var scopeless = CreateExternalClient(f.Factory, plaintext);

        var response = await scopeless.GetAsync(MessageUrl(messageId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("external.auth.scope_missing", (await BodyAsync(response)).GetProperty("code").GetString());
    }
}
