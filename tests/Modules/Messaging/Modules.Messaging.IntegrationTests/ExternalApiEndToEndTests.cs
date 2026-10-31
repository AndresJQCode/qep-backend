using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Modules.ApiAccess.Application;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-30 §7: la función de punta a punta, con el <c>ApiKeyConnectionDirectory</c> real y sin dobles. Lo que amarran: la
/// clave nace en la administración y funciona en la API externa, el secreto no sale a ningún rastro, y una clave no toca a otro tenant.
/// </summary>
public sealed class ExternalApiEndToEndTests
{
    private const string Phone = "573001234567";
    private const string TemplateName = "aviso_despacho";
    private const string Language = "es_CO";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu pedido {{2}} va en camino."}]}
        ]}
        """;

    private const string MetaOk =
        """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.e2e"}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        WebApplicationFactory<Program> Host, FakeMetaGraphHandler Meta, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin, CapturedLogs Logs);

    private sealed record CreatedKey(Guid Id, string Plaintext);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool traceLogs = false)
    {
        var connectionString = database.GetConnectionString();
        var logs = new CapturedLogs();
        var factory = new QepApiFactory(connectionString);
        WebApplicationFactory<Program> host = factory.WithCapturedLogs(logs);
        if (traceLogs)
        {
            // Las reglas de appsettings (Default Information, Microsoft.AspNetCore Warning) ganan sobre SetMinimumLevel; una regla del
            // proveedor de la prueba sin categoría gana sobre todas ellas para ese proveedor, así que lo captura todo desde Trace.
            host = host.WithWebHostBuilder(builder =>
                builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFilter<CapturedLogs>(null, LogLevel.Trace)));
        }

        var tenant = await RegisterTenantAsync(host);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, MetaOk);
        var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ApiAccessPermissions.ApiKeyManage);
        return new Fixture(host, factory.MetaHandler, connectionString, tenant, connectionId, admin, logs);
    }

    private static string AdminUrl(Guid tenantId, string suffix = "") => $"/api/v1/tenants/{tenantId}/api-keys{suffix}";

    private static async Task<CreatedKey> CreateKeyAsync(Fixture f, string name, Guid[]? connectionIds = null)
    {
        var response = await f.Admin.PostAsJsonAsync(
            AdminUrl(f.Tenant.TenantId),
            new { name, scopes = new[] { MessagingScopes.TemplatesSend }, connectionIds, expiresAt = (DateTimeOffset?)null },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new CreatedKey(body.GetProperty("key").GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    private static object Body(Guid clientId, Guid connectionId, string parameter = "4821") => new
    {
        clientId,
        connectionId,
        to = Phone,
        template = new { name = TemplateName, language = Language, parameters = new Dictionary<string, string> { ["1"] = "Ana", ["2"] = parameter } },
    };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, object body) => client.PostAsJsonAsync(ExternalMessagesUrl, body, Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static Task<string> RowsAsync(string connectionString, string table) =>
        ScalarAsync<string>(connectionString, $"SELECT coalesce(string_agg(row_to_json(t)::text, E'\\n'), '') FROM {table} AS t");

    /// <summary>La parte secreta de <c>qep_&lt;prefijo&gt;_&lt;secreto&gt;</c>: lo único que no es público (el prefijo sale en la lista).</summary>
    private static string SecretPart(string plaintext) => plaintext[13..];

    [Fact]
    public async Task TheSecretNeverAppearsInLogsAuditOrRequestFailures()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, traceLogs: true);
        using var _ = f.Host;
        var key = await CreateKeyAsync(f, "ERP Contable");
        var wrong = key.Plaintext[..13] + new string('A', 43);
        using var client = CreateExternalClient(f.Host, key.Plaintext);
        using var wrongClient = CreateExternalClient(f.Host, wrong);

        var ok = await SendAsync(client, Body(Guid.CreateVersion7(), f.ConnectionId));
        var invalid = await SendAsync(client, Body(Guid.CreateVersion7(), f.ConnectionId, parameter: ""));
        var unauthorized = await SendAsync(wrongClient, Body(Guid.CreateVersion7(), f.ConnectionId));
        var revoke = await f.Admin.PostAsync(AdminUrl(f.Tenant.TenantId, $"/{key.Id}/revoke"), content: null, Ct);
        var afterRevoke = await SendAsync(client, Body(Guid.CreateVersion7(), f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        // Controles positivos: el host registra desde Trace, la auditoría y las fallas de request sí se escriben, y el mensaje existe.
        Assert.Contains(LogLevel.Trace, f.Logs.Levels);
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries") > 0);
        Assert.True(await CountAsync(f.ConnectionString, "SELECT count(*) FROM platform.request_failures") > 0);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE sent_by_kind = 4"));

        var texts = new Dictionary<string, string> { ["logs"] = f.Logs.AllText };
        foreach (var table in new[] { "audit.entries", "platform.request_failures", "api_access.api_keys", "messaging.messages" })
        {
            texts[table] = await RowsAsync(f.ConnectionString, table);
        }

        foreach (var (source, text) in texts)
        {
            foreach (var secret in new[] { key.Plaintext, SecretPart(key.Plaintext), wrong, SecretPart(wrong) })
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"El secreto aparece en {source}.");
            }
        }
    }

    [Fact]
    public async Task AKeyCreatedInTheAdminWorksEndToEnd()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Host;
        var key = await CreateKeyAsync(f, "ERP Contable");
        using var client = CreateExternalClient(f.Host, key.Plaintext);

        var connections = await client.GetFromJsonAsync<JsonElement>(ExternalConnectionsUrl, Ct);
        var templates = await client.GetFromJsonAsync<JsonElement>(ExternalTemplatesUrl(f.ConnectionId), Ct);
        var sent = await SendAsync(client, Body(Guid.CreateVersion7(), f.ConnectionId));

        Assert.Equal(f.ConnectionId, Assert.Single(connections.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(TemplateName, Assert.Single(templates.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var body = await BodyAsync(sent);
        var conversationId = body.GetProperty("conversationId").GetGuid();

        using var reader = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var thread = await reader.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, conversationId), Ct);
        var message = Assert.Single(thread.GetProperty("items").EnumerateArray());
        Assert.Equal(key.Id, message.GetProperty("sentByApiKey").GetProperty("id").GetGuid());
        Assert.Equal("ERP Contable", message.GetProperty("sentByApiKey").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("sentBy").ValueKind);
    }

    [Fact]
    public async Task AKeyOfTenantACannotTouchTenantB()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Host;
        var tenantB = await RegisterTenantAsync(f.Host);
        await EnableDeveloperAsync(f.ConnectionString, tenantB.TenantId);
        await EnableMessagingAsync(f.ConnectionString, tenantB.TenantId);
        var connectionB = await SeedWhatsAppConnectionAsync(f.Host, tenantB.TenantId, "Ajena", "333", "444");
        f.Meta.Respond("/444/message_templates", HttpStatusCode.OK, Catalog);
        f.Meta.Respond("/333/messages$", HttpStatusCode.OK, MetaOk);
        var memberB = await OwnerMembershipIdAsync(f.ConnectionString, tenantB);
        var (_, plaintextB) = await SeedApiKeyAsync(f.ConnectionString, tenantB.TenantId, memberB, [MessagingScopes.TemplatesSend], name: "Clave B");
        using var clientB = CreateExternalClient(f.Host, plaintextB);
        var sharedClientId = Guid.CreateVersion7();
        var sentByB = await SendAsync(clientB, Body(sharedClientId, connectionB));
        Assert.Equal(HttpStatusCode.Created, sentByB.StatusCode);
        var messageOfB = (await BodyAsync(sentByB)).GetProperty("messageId").GetGuid();

        // Clave de A sin lista de conexiones (connection_ids NULL): "todas las mías" no incluye las de B.
        var keyA = await CreateKeyAsync(f, "Clave A");
        using var clientA = CreateExternalClient(f.Host, keyA.Plaintext);
        var intoB = await SendAsync(clientA, Body(Guid.CreateVersion7(), connectionB));
        var templatesOfB = await clientA.GetAsync(ExternalTemplatesUrl(connectionB), Ct);
        var connections = await clientA.GetFromJsonAsync<JsonElement>(ExternalConnectionsUrl, Ct);
        // El clientId de B tampoco hace replay para A: la idempotencia es por tenant.
        var withBClientId = await SendAsync(clientA, Body(sharedClientId, f.ConnectionId));

        foreach (var response in new[] { intoB, templatesOfB })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("external.connection_not_allowed", (await BodyAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(f.ConnectionId, Assert.Single(connections.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, withBClientId.StatusCode);
        Assert.NotEqual(messageOfB, (await BodyAsync(withBClientId)).GetProperty("messageId").GetGuid());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE tenant_id = @t AND direction = 2", ("t", tenantB.TenantId)));
    }

    [Fact]
    public async Task TurningMessagingOffAndOnKeepsTheKeyWorking()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Host;
        var key = await CreateKeyAsync(f, "ERP Contable");
        using var client = CreateExternalClient(f.Host, key.Plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var off = await client.GetAsync(ExternalConnectionsUrl, Ct);
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'active' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var on = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await BodyAsync(off)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
    }

    [Fact]
    public async Task OpenApiDescribesTheExternalRoutes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Host;
        using var client = f.Host.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        var paths = document.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        Assert.Contains("/api/v1/external/whatsapp/connections", paths);
        Assert.Contains("/api/v1/external/whatsapp/connections/{connectionId}/templates", paths);
        Assert.Contains("/api/v1/external/whatsapp/messages", paths);
    }
}
