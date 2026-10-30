using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Criterio 3 del spec 2026-10-08 (viene de <c>WhatsAppSecretLeakTests</c>, 6612298): ninguna
/// respuesta, log, auditoría, outbox ni <c>platform.request_failures</c> contiene el valor de un
/// secreto. Tres fallas que llevan un token centinela adentro —un 422 del validador, el 503 sin llave
/// activa y un 401 de Zenvia que repite el token en su cuerpo— y un ciclo de vida completo que además
/// no puede dejar un valor de campo (ni <c>fromNumber</c>) en auditoría u outbox.
/// </summary>
public sealed class ConnectionSecretLeakTests
{
    private const string SecondSentinel = "zenvia-token-SENTINEL-2b8e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertNothingLeaksAsync(
        string connectionString, CapturedLogs logs, IEnumerable<string> responseBodies, params string[] values)
    {
        var failures = await RequestFailuresTextAsync(connectionString);
        var trail = await AuditAndOutboxTextAsync(connectionString);
        var bodies = string.Join('\n', responseBodies);
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, bodies, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, failures, StringComparison.Ordinal);
            Assert.DoesNotContain(value, trail, StringComparison.Ordinal);
        }
    }

    // Control positivo: sin esto, un CapturedLogs que no engancha el host vuelve vacía la ausencia.
    private static void AssertLogged(CapturedLogs logs, string expectedFragment) =>
        Assert.Contains(logs.Entries, entry => entry.Contains(expectedFragment, StringComparison.Ordinal));

    private static Task<long> FailuresWithStatusAsync(string connectionString, int status) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM platform.request_failures WHERE status_code = {status}");

    [Fact]
    public async Task AValidationFailureCarryingTheTokenDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody(name: new string('x', 81)));
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(connectionString, 422) >= 1);
        AssertLogged(logs, "API request failed with code");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken);
    }

    // Sin llave activa: 503. Su mensaje nombra la clave de configuración, no el token.
    [Fact]
    public async Task AServiceUnavailableWithoutAnActiveKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(connectionString, 503) >= 1);
        AssertLogged(logs, "API request failed with code");
        Assert.Contains("integrations.secret_protection.unavailable", await RequestFailuresTextAsync(connectionString) + body, StringComparison.Ordinal);
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken);
    }

    // Zenvia responde 401 con un cuerpo que, por si acaso, repite el token: ninguno de los dos sale.
    [Fact]
    public async Task ZenviaRejectingTheCredentialsDoesNotLeakTheTokenNorItsBody()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        factory.ZenviaHandler.Body = JsonSerializer.Serialize(new { message = SentinelZenviaBody, echo = SentinelApiToken });
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.credentials_rejected", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.Equal(SentinelApiToken, Assert.Single(factory.ZenviaHandler.Requests).Token);
        AssertLogged(logs, "Zenvia credential test answered HTTP 401");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken, SentinelZenviaBody);
    }

    // Spec, «Nunca en un log»: el HttpClient del módulo no lleva los loggers de IHttpClientFactory
    // (RemoveAllLoggers), que registran método, URL y, con nivel Trace, headers. Se prueba por la
    // categoría de cada entrada: ninguna sale de System.Net.Http.HttpClient.* durante una llamada real
    // a la prueba de Zenvia (el handler primario falso sólo reemplaza al último eslabón de la cadena).
    [Fact]
    public async Task TheZenviaClientCarriesNoHttpClientFactoryLoggers()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(factory.ZenviaHandler.Requests);
        AssertLogged(logs, "Zenvia credential test answered HTTP 200");
        Assert.DoesNotContain(logs.Categories, category => category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }

    // Zenvia caído: la excepción de red trae el token en su mensaje y ni la respuesta ni los logs ni la
    // falla guardada pueden repetirlo (el tester sólo registra «network»).
    [Fact]
    public async Task AProviderThatThrowsWithTheTokenInTheMessageDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        factory.ZenviaHandler.Throw = new HttpRequestException($"connection refused for token {SentinelApiToken}");
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.provider_unreachable", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        AssertLogged(logs, "Zenvia credential test could not reach the provider");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken);
    }

    // Zenvia responde 5xx con un cuerpo que repite el token: no se lee, y no sale por ningún camino.
    [Fact]
    public async Task AServerErrorFromTheProviderDoesNotLeakTheTokenNorItsBody()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        factory.ZenviaHandler.Status = HttpStatusCode.BadGateway;
        factory.ZenviaHandler.Body = JsonSerializer.Serialize(new { message = SentinelZenviaBody, echo = SentinelApiToken });
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.provider_unreachable", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        AssertLogged(logs, "Zenvia credential test answered HTTP 502");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken, SentinelZenviaBody);
    }

    // Llave retirada: el secreto guardado ya no descifra. GET lo marca readable=false y POST /test
    // responde 422 en el campo (P14); en ningún caso sale el token ni el ciphertext.
    [Fact]
    public async Task AStoredSecretThatNoLongerDecryptsDoesNotLeakAnything()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var creator = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(creator, tenant.TenantId);
        var otherKey = Convert.ToBase64String(Enumerable.Range(200, 32).Select(index => (byte)index).ToArray());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection("other", ("other", otherKey), ("test", string.Empty)).WithCapturedLogs(logs);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        var read = await SendAsync(client, HttpMethod.Get, url);
        var readBody = await read.Content.ReadAsStringAsync(Ct);
        var test = await SendAsync(client, HttpMethod.Post, $"{url}/test");
        var testBody = await test.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(JsonDocument.Parse(readBody).RootElement.GetProperty("secrets").GetProperty("apiToken").GetProperty("readable").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, test.StatusCode);
        Assert.Contains("secrets.apiToken", testBody, StringComparison.Ordinal);
        await AssertNothingLeaksAsync(connectionString, logs, [readBody, testBody], SentinelApiToken);
    }

    // Spec 2026-10-09 §11: el code, el token de acceso, el AppSecret y el PIN no salen por ningún camino,
    // ni cuando Graph falla repitiéndolos en el cuerpo.
    [Fact]
    public async Task EmbeddedSignupNeverLeaksTheCodeTheTokenTheAppSecretNorThePin()
    {
        const string sentinelPin = "907316";
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        ScriptHappySignup(factory.MetaHandler);
        factory.MetaHandler.Reset();
        factory.MetaHandler.Respond("/oauth/access_token", HttpStatusCode.OK, $$"""{"access_token":"{{SentinelMetaAccessToken}}"}""");
        factory.MetaHandler.Respond("/111/register", HttpStatusCode.BadRequest, $$$"""{"error":{"message":"{{{SentinelMetaCode}}} {{{SentinelMetaAccessToken}}} {{{SentinelMetaAppSecret}}}","code":100}}""");
        var tenant = await RegisterTenantAsync(host);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var failed = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody(pin: sentinelPin));
        var failedBody = await failed.Content.ReadAsStringAsync(Ct);
        factory.MetaHandler.Reset();
        ScriptHappySignup(factory.MetaHandler);
        var created = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody(pin: sentinelPin));
        var createdBody = await created.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertLogged(logs, "Graph register answered HTTP 400");
        await AssertNothingLeaksAsync(connectionString, logs, [failedBody, createdBody], SentinelMetaCode, SentinelMetaAccessToken, SentinelMetaAppSecret, sentinelPin);
    }

    // Addendum 2026-10-30 del spec 2026-10-29: el verify_token viaja en el cuerpo de subscribed_apps. Ni un rechazo de Meta que lo
    // repita, ni el 201 de después, lo dejan en la respuesta, los logs, la auditoría, el outbox o las fallas guardadas.
    [Fact]
    public async Task TheWebhookOverrideNeverLeaksTheVerifyToken()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithWebhookOverride(WebhookOverrideUrl).WithCapturedLogs(logs);
        factory.MetaHandler.Respond("/222/subscribed_apps", HttpStatusCode.BadRequest, $$$"""{"error":{"message":"challenge failed for {{{SentinelMetaVerifyToken}}}","code":2200}}""");
        ScriptHappySignup(factory.MetaHandler);
        var tenant = await RegisterTenantAsync(host);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var failed = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var failedBody = await failed.Content.ReadAsStringAsync(Ct);
        factory.MetaHandler.Reset();
        ScriptHappySignup(factory.MetaHandler);
        var created = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var createdBody = await created.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // Control positivo: el token sí salió hacia Meta, así que su ausencia en el resto significa algo.
        Assert.Contains(factory.MetaHandler.Requests, request => request.Body?.Contains(SentinelMetaVerifyToken, StringComparison.Ordinal) == true);
        AssertLogged(logs, "Graph subscribed-apps answered HTTP 400");
        await AssertNothingLeaksAsync(connectionString, logs, [failedBody, createdBody], SentinelMetaVerifyToken);
    }

    // Spec 2026-10-15 §12: el token de la Admin API (y el code y el client secret) no salen por ningún camino en el enlace,
    // el probador ni un rechazo de Shopify que repite el token en su cuerpo.
    [Fact]
    public async Task ShopifyLinkAndTestNeverLeakTheTokenTheCodeNorTheClientSecret()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var tenant = await RegisterTenantAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var installId = await ShopifyFlow.InstallAsync(host, client, tenant.TenantId);

        var linked = await SendAsync(client, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), new { installId });
        var linkedBody = await linked.Content.ReadAsStringAsync(Ct);
        var id = (await linked.Content.ReadFromJsonAsync<ConnectionResponse>(Ct))!.Id;
        var tested = await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, id)}/test");
        var testedBody = await tested.Content.ReadAsStringAsync(Ct);
        factory.ShopifyHandler.Reset();
        factory.ShopifyHandler.Respond(
            "/graphql\\.json$", HttpStatusCode.Unauthorized,
            $$"""{"errors":"[API] Invalid API key or access token ({{ShopifyFlow.SentinelAccessToken}})"}""");
        var rejected = await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, id)}/test");
        var rejectedBody = await rejected.Content.ReadAsStringAsync(Ct);
        var read = await SendAsync(client, HttpMethod.Get, ConnectionUrl(tenant.TenantId, id));
        var readBody = await read.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("credentials_rejected", rejectedBody, StringComparison.Ordinal);
        AssertLogged(logs, "Shopify webhook-create answered HTTP 200");
        AssertLogged(logs, "Shopify shop answered HTTP 401");
        var fields = await ScalarAsync<string>(connectionString, "SELECT fields::text FROM integrations.connections WHERE id = @id", ("id", id));
        Assert.DoesNotContain(ShopifyFlow.SentinelAccessToken, fields, StringComparison.Ordinal);
        await AssertNothingLeaksAsync(
            connectionString, logs, [linkedBody, testedBody, rejectedBody, readBody],
            ShopifyFlow.SentinelAccessToken, ShopifyFlow.SentinelCode, SentinelShopifyClientSecret);
    }

    // Spec 2026-10-21 D-H5: la API externa responde 401 con un cuerpo que repite la clave; ni la clave ni el cuerpo salen por la
    // respuesta, los logs, la auditoría ni platform.request_failures. La clave sí viajó en su header: es el probador real.
    [Fact]
    public async Task AnExternalApiKeyThatTheProviderEchoesNeverLeaks()
    {
        const string sentinelBody = "external-api-body-SENTINEL-6f2e";
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        factory.ExternalApiHandler.Status = HttpStatusCode.Unauthorized;
        factory.ExternalApiHandler.Body = JsonSerializer.Serialize(new { message = sentinelBody, echo = SentinelExternalApiKey });
        var tenant = await RegisterAutomationsTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ExternalApiBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.credentials_rejected", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        var request = Assert.Single(factory.ExternalApiHandler.Requests);
        Assert.Equal("https://api-clasic.qcode.co/", request.Uri?.ToString());
        Assert.Equal(SentinelExternalApiKey, request.Headers["x-api-key"]);
        AssertLogged(logs, "External API credential test answered HTTP 401");
        Assert.DoesNotContain(logs.Categories, category => category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelExternalApiKey, sentinelBody);
    }

    // Spec, «Auditoría»: nunca un valor de campo, ni público. Y el token nunca en fields.
    [Fact]
    public async Task AWholeLifecycleNeverWritesASecretOrAFieldValueOutsideTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var tenant = await RegisterZenviaTenantAsync(host, connectionString);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var bodies = new List<string>();

        async Task<HttpResponseMessage> CallAsync(HttpMethod method, string url, object? body = null, string? ifMatch = null)
        {
            var response = await SendAsync(client, method, url, body, ifMatch);
            bodies.Add(await response.Content.ReadAsStringAsync(Ct));
            return response;
        }

        var created = await CallAsync(HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var id = (await created.Content.ReadFromJsonAsync<ConnectionResponse>(Ct))!.Id;
        var url = ConnectionUrl(tenant.TenantId, id);
        (await CallAsync(HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999", ["templateId"] = IntegrationsApiHarness.TemplateId },
            secrets = new Dictionary<string, string?> { ["apiToken"] = SecondSentinel },
        }, "\"1\"")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/test")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/pause", ifMatch: "\"3\"")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/resume", ifMatch: "\"4\"")).EnsureSuccessStatusCode();

        var fields = await ScalarAsync<string>(connectionString, "SELECT fields::text FROM integrations.connections WHERE id = @id", ("id", id));
        Assert.DoesNotContain(SentinelApiToken, fields, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondSentinel, fields, StringComparison.Ordinal);

        (await CallAsync(HttpMethod.Delete, url, ifMatch: "\"5\"")).EnsureSuccessStatusCode();

        AssertLogged(logs, "Zenvia credential test answered HTTP 200");
        await AssertNothingLeaksAsync(connectionString, logs, bodies, SentinelApiToken, SecondSentinel);
        var trail = await AuditAndOutboxTextAsync(connectionString);
        Assert.Contains("fromNumber", trail, StringComparison.Ordinal);
        Assert.DoesNotContain(FromNumber, trail, StringComparison.Ordinal);
        Assert.DoesNotContain("573009999999", trail, StringComparison.Ordinal);
    }
}
