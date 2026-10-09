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
        AssertLogged(logs, "Unhandled API exception");
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
