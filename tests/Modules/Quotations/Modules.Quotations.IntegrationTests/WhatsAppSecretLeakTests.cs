using System.Net;
using System.Text.Json;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Criterio 5 del spec 2026-10-07: la API key —y el cuerpo crudo de la respuesta de la cuenta
/// propia— no salen por ningún camino. Tres fallas que llevan una key centinela por adentro (un
/// 422 del validador, un 500 sin llave activa y un 401 de Zenvia en el envío), y después de cada
/// una: ni la respuesta, ni los logs capturados, ni platform.request_failures la contienen. La
/// traza de OTel no se lee: RecordException registra el mismo mensaje y la misma cadena de
/// excepciones que estos caminos.
/// </summary>
public sealed class WhatsAppSecretLeakTests
{
    private static async Task AssertNothingLeaksAsync(
        string connectionString, CapturedLogs logs, string responseBody, params string[] secrets)
    {
        var failures = await RequestFailuresTextAsync(connectionString);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, failures, StringComparison.Ordinal);
        }
    }

    // Control positivo: sin esto, un CapturedLogs que no engancha el host vuelve vacía la ausencia.
    private static void AssertLogged(CapturedLogs logs, string expectedFragment) =>
        Assert.Contains(logs.Entries, entry => entry.Contains(expectedFragment, StringComparison.Ordinal));

    private static Task<long> FailuresWithStatusAsync(string connectionString, int status) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM platform.request_failures WHERE status_code = {status}");

    [Fact]
    public async Task AValidationFailureCarryingTheKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SettingsPermissions);

        var response = await PutSettingsAsync(
            client,
            tenantId,
            new { mode = "Own", provider = "Zenvia", apiKey = SentinelApiKey, fromNumber = FromNumber, templateId = "no-es-un-guid" },
            "\"1\"");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(database.GetConnectionString(), 422) >= 1);
        AssertLogged(logs, "API request failed with code");
        await AssertNothingLeaksAsync(database.GetConnectionString(), logs, body, SentinelApiKey);
    }

    // Sin llave activa, Protect lanza: es el 500 que el spec acepta fuera de producción. Su mensaje
    // nombra la clave de configuración, no la key.
    [Fact]
    public async Task AServerErrorWhileProtectingTheKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SettingsPermissions);

        var response = await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(database.GetConnectionString(), 500) >= 1);
        AssertLogged(logs, "Unhandled API exception");
        // El 500 tiene que venir de Protect (sin llave activa), no de otra falla cualquiera.
        Assert.Contains("SecretProtection", await RequestFailuresTextAsync(database.GetConnectionString()), StringComparison.Ordinal);
        await AssertNothingLeaksAsync(database.GetConnectionString(), logs, body, SentinelApiKey);
    }

    // Zenvia responde 401 con un cuerpo que, por si acaso, repite la key: ninguno de los dos sale.
    [Fact]
    public async Task ZenviaRejectingTheOwnCredentialsDoesNotLeakTheKeyNorItsBody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        var zenviaBody = JsonSerializer.Serialize(new { message = SentinelZenviaBody, echo = SentinelApiKey });
        var zenvia = new CapturingZenviaHandler(HttpStatusCode.Unauthorized, zenviaBody);
        using var host = factory.WithZenviaHandler(zenvia).WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "quotation.whatsapp.credentials_rejected",
            JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.Equal(SentinelApiKey, Assert.Single(zenvia.Requests).Token);
        Assert.True(await FailuresWithStatusAsync(database.GetConnectionString(), 422) >= 1);
        AssertLogged(logs, "API request failed with code");
        await AssertNothingLeaksAsync(
            database.GetConnectionString(), logs, body, SentinelApiKey, SentinelZenviaBody);
    }
}
