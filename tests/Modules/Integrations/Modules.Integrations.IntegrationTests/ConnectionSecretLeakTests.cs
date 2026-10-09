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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(host);
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
        var tenant = await RegisterTenantAsync(factory);
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

    // Spec 2026-10-09 §11: el code, el token de acceso y el AppSecret no salen por ningún camino,
    // ni cuando Graph falla repitiéndolos en el cuerpo.
    [Fact]
    public async Task EmbeddedSignupNeverLeaksTheCodeTheTokenNorTheAppSecret()
    {
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

        var failed = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var failedBody = await failed.Content.ReadAsStringAsync(Ct);
        factory.MetaHandler.Reset();
        ScriptHappySignup(factory.MetaHandler);
        var created = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var createdBody = await created.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertLogged(logs, "Graph register answered HTTP 400");
        await AssertNothingLeaksAsync(connectionString, logs, [failedBody, createdBody], SentinelMetaCode, SentinelMetaAccessToken, SentinelMetaAppSecret);
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
        var tenant = await RegisterTenantAsync(host);
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
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
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
