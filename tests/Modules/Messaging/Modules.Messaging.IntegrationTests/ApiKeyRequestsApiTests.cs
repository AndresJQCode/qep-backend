using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.ApiAccess.Application;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-11-01 parte 1 (Lectura) por HTTP y contra la base real: <c>GET /api/v1/tenants/{id}/api-keys/requests</c> lista las
/// llamadas del tenant con el estado actual de su mensaje, y una llamada 201 cuyo mensaje falló después cuenta como falla.
/// </summary>
public sealed class ApiKeyRequestsApiTests
{
    private const string Phone = "573001234567";
    private const string Template = "aviso_despacho";
    private const string Language = "es_CO";
    private const string CountRowsSql = "SELECT count(*) FROM api_access.api_key_requests WHERE tenant_id = @tenantId";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Tu pedido va en camino."}]}
        ]}
        """;

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId, Guid KeyId, HttpClient Sender, HttpClient Admin);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (keyId, plaintext) = await SeedApiKeyAsync(connectionString, tenant.TenantId, member, SendScope);
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptMetaOk(factory.MetaHandler, "111");
        var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ApiAccessPermissions.ApiKeyManage);
        return new Fixture(factory, connectionString, tenant, member, connectionId, keyId, CreateExternalClient(factory, plaintext), admin);
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

    private static string RequestsUrl(Guid tenantId, string query = "") => $"/api/v1/tenants/{tenantId}/api-keys/requests{query}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static async Task<Guid> SendAsync(Fixture f)
    {
        var response = await f.Sender.PostAsJsonAsync(ExternalMessagesUrl, Body(f.ConnectionId), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("messageId").GetGuid();
    }

    /// <summary>Un 422: no lleva mensaje, y cuenta como falla por el HTTP solo.</summary>
    private static async Task SendInvalidAsync(Fixture f) =>
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await f.Sender.PostAsJsonAsync(ExternalMessagesUrl, new { connectionId = f.ConnectionId, to = Phone }, Ct)).StatusCode);

    /// <summary>La fila se escribe después de responder: se espera sondeando.</summary>
    private static async Task WaitForRowsAsync(Fixture f, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountAsync(f.ConnectionString, CountRowsSql, ("tenantId", f.Tenant.TenantId)) >= expected)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"No aparecieron {expected} filas en api_access.api_key_requests.");
    }

    private static async Task<JsonElement> ListAsync(Fixture f, string query = "")
    {
        var response = await f.Admin.GetAsync(RequestsUrl(f.Tenant.TenantId, query), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    [Fact]
    public async Task ALateFailureReadsAsFailureOnA201Row()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var messageId = await SendAsync(f);
        await WaitForRowsAsync(f, 1);
        using var webhookClient = f.Factory.CreateClient();
        await PostWebhookAsync(webhookClient, MetaPayloads.Status("111", "wamid.111.1", "failed", 1760000200, $"qep:{messageId:D}", errorCode: 131042));
        await DrainDeliveriesAsync(f.Factory);

        var all = await ListAsync(f);
        var failures = await ListAsync(f, "?result=failure");
        var successes = await ListAsync(f, "?result=success");

        var row = Assert.Single(all.GetProperty("items").EnumerateArray());
        Assert.Equal(201, row.GetProperty("statusCode").GetInt32());
        Assert.Equal(messageId, row.GetProperty("messageId").GetGuid());
        Assert.Equal("Failed", row.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal(131042, row.GetProperty("message").GetProperty("failureCode").GetInt32());
        Assert.Equal(1, failures.GetProperty("total").GetInt32());
        Assert.Equal(0, successes.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AFailureFilterWithoutFailedMessagesStillReturnsHttpFailures()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SendAsync(f);
        await SendInvalidAsync(f);
        await WaitForRowsAsync(f, 2);

        var failures = await ListAsync(f, "?result=failure");
        var successes = await ListAsync(f, "?result=success");

        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());
        Assert.Equal(422, failure.GetProperty("statusCode").GetInt32());
        var success = Assert.Single(successes.GetProperty("items").EnumerateArray());
        Assert.Equal(201, success.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task TheTotalMatchesTheFilterAcrossPages()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SendAsync(f);
        await SendAsync(f);
        await SendInvalidAsync(f);
        await WaitForRowsAsync(f, 3);

        var first = await ListAsync(f, "?pageSize=1&page=1");
        var third = await ListAsync(f, "?pageSize=1&page=3");
        var filtered = await ListAsync(f, "?result=success&pageSize=1");

        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());
        Assert.Single(third.GetProperty("items").EnumerateArray());
        Assert.Equal(2, filtered.GetProperty("total").GetInt32());
        Assert.Single(filtered.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AKeyOfAnotherTenantReturnsNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var other = await ArrangeOtherTenantAsync(f);
        await SendAsync(other);
        await WaitForRowsAsync(other, 1);

        var read = await ListAsync(f, $"?apiKeyId={other.KeyId}");

        Assert.Equal(0, read.GetProperty("total").GetInt32());
        Assert.Empty(read.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ARowsOfAnotherTenantAreNotListed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var other = await ArrangeOtherTenantAsync(f);
        await SendAsync(f);
        await SendAsync(other);
        await SendAsync(other);
        await WaitForRowsAsync(f, 1);
        await WaitForRowsAsync(other, 2);

        var read = await ListAsync(f);

        Assert.Equal(1, read.GetProperty("total").GetInt32());
        var row = Assert.Single(read.GetProperty("items").EnumerateArray());
        Assert.Equal(f.KeyId, row.GetProperty("apiKeyId").GetGuid());
    }

    [Fact]
    public async Task WithoutThePermissionIsA403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var withoutPermission = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId);

        var response = await withoutPermission.GetAsync(RequestsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithTheModuleOffIsA403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ApiAccessPermissions.ApiKeyManage);

        var response = await admin.GetAsync(RequestsUrl(tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownResultIsA422WithItsField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await f.Admin.GetAsync(RequestsUrl(f.Tenant.TenantId, "?result=maybe"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("result", out _));
    }

    [Fact]
    public async Task TheResponseCarriesNoStackTrace()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SendAsync(f);
        await SendInvalidAsync(f);
        await WaitForRowsAsync(f, 2);

        var ok = await (await f.Admin.GetAsync(RequestsUrl(f.Tenant.TenantId), Ct)).Content.ReadAsStringAsync(Ct);
        var failed = await (await f.Admin.GetAsync(RequestsUrl(f.Tenant.TenantId, "?result=maybe"), Ct)).Content.ReadAsStringAsync(Ct);

        foreach (var json in new[] { ok, failed })
        {
            Assert.DoesNotContain("at ", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        }
    }

    /// <summary>Otro tenant en la misma base, con su conexión, su clave y su administrador.</summary>
    private static async Task<Fixture> ArrangeOtherTenantAsync(Fixture f)
    {
        var tenant = await RegisterTenantAsync(f.Factory);
        await EnableDeveloperAsync(f.ConnectionString, tenant.TenantId);
        await EnableMessagingAsync(f.ConnectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(f.ConnectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(f.Factory, tenant.TenantId, "Ajena", "333", "444");
        var (keyId, plaintext) = await SeedApiKeyAsync(f.ConnectionString, tenant.TenantId, member, SendScope);
        f.Factory.MetaHandler.Respond("/444/message_templates", HttpStatusCode.OK, Catalog);
        ScriptMetaOk(f.Factory.MetaHandler, "333");
        var admin = CreateClient(f.Factory, tenant.OwnerUserId, tenant.TenantId, ApiAccessPermissions.ApiKeyManage);
        return new Fixture(f.Factory, f.ConnectionString, tenant, member, connectionId, keyId, CreateExternalClient(f.Factory, plaintext), admin);
    }
}
