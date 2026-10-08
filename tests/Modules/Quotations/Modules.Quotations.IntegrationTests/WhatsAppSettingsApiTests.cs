using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Api;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Endpoints» y «Pruebas → Integración»: el GET sin fila, el primer guardado Own
/// sin que la key vuelva nunca, conservar el texto cifrado sin apiKey y al ir y volver de Own, 428
/// y 412, el no-op de Shared sin fila, Disabled sobre bytes dañados (200, no 500), los cuatro
/// faltantes, los 403 y el gate de capacidad con un tenant real.
/// </summary>
public sealed class WhatsAppSettingsApiTests
{
    // CA1861: arreglos constantes como campos, no argumentos en línea.
    private static readonly string[] ExpectedModes = ["Shared", "Own", "Disabled"];
    private static readonly string[] ExpectedProviders = ["Zenvia"];
    private static readonly string[] ExpectedMissingFields = ["ApiKey", "FromNumber", "Provider", "TemplateId"];

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    private static Task<byte[]> CiphertextAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<byte[]>(
            connectionString,
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    [Fact]
    public async Task WithoutARowTheSettingsAreSharedAtVersionOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var body = await JsonAsync(response);
        Assert.Equal("Shared", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("provider").ValueKind);
        Assert.False(body.GetProperty("apiKeyConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiKeyReadable").ValueKind);
        Assert.Equal(
            ExpectedModes,
            body.GetProperty("modes").EnumerateArray().Select(mode => mode.GetString()).ToArray());
        Assert.Equal(
            ExpectedProviders,
            body.GetProperty("providers").EnumerateArray().Select(provider => provider.GetString()).ToArray());
        Assert.Equal(1, body.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task TheFirstOwnSaveReturnsETagTwoAndTheKeyNeverComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var put = await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"");
        var putRaw = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);
        var getRaw = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("\"2\"", put.Headers.ETag?.Tag);
        Assert.DoesNotContain(SentinelApiKey, putRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelApiKey, getRaw, StringComparison.Ordinal);
        var body = JsonDocument.Parse(getRaw).RootElement;
        Assert.Equal("Own", body.GetProperty("mode").GetString());
        Assert.True(body.GetProperty("apiKeyConfigured").GetBoolean());
        Assert.True(body.GetProperty("apiKeyReadable").GetBoolean());
        Assert.Equal(FromNumber, body.GetProperty("fromNumber").GetString());
        Assert.Equal(TemplateId, body.GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task APutWithoutApiKeyKeepsTheCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var before = await CiphertextAsync(database.GetConnectionString(), tenantId);

        var response = await PutSettingsAsync(
            client, tenantId, new { mode = "Own", templateId = "11111111-2222-3333-4444-555555555555" }, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CiphertextAsync(database.GetConnectionString(), tenantId));
    }

    // Criterio 4: Own → Shared → Own no obliga a reescribir las credenciales.
    [Fact]
    public async Task OwnToSharedAndBackKeepsTheSameCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var before = await CiphertextAsync(database.GetConnectionString(), tenantId);

        (await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"2\"")).EnsureSuccessStatusCode();
        var back = await PutSettingsAsync(client, tenantId, new { mode = "Own" }, "\"3\"");

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("Own", (await JsonAsync(back)).GetProperty("mode").GetString());
        Assert.Equal(before, await CiphertextAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task APutWithoutIfMatchIsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, ifMatch: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("precondition.if_match_required", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SharedWithoutARowIsANoOpThatCreatesNoRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await JsonAsync(response)).GetProperty("version").GetInt64());
        Assert.Equal(0L, await ScalarAsync<long>(
            database.GetConnectionString(),
            $"SELECT count(*) FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'"));
    }

    // Decisión 25: el drenaje se salta y el guardado no es un 500.
    [Fact]
    public async Task DisabledOverDamagedBytesSavesAndTheKeyIsReportedUnreadable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE quotations.tenant_whatsapp_settings "
            + "SET api_token_ciphertext = set_byte(api_token_ciphertext, 20, get_byte(api_token_ciphertext, 20) # 255) "
            + $"WHERE tenant_id = '{tenantId}'");

        var put = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"2\"");
        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await JsonAsync(get)).GetProperty("apiKeyReadable").GetBoolean());
    }

    [Fact]
    public async Task OwnWithoutARowListsTheFourMissingFields()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Own" }, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", body.GetProperty("code").GetString());
        Assert.Equal(
            ExpectedMissingFields,
            body.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task WithoutSettingsUpdateThePutIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, TenancyPermissions.SettingsRead);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            WhatsAppSettingsUrl(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El stub con un tenant inventado no ejercita el guard (FindAsync en null): hace falta uno real
    // al que se le quita quotations.
    [Fact]
    public async Task WithoutTheQuotationsModuleTheSettingsAreModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);
        var put = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await JsonAsync(get)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await JsonAsync(put)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheChannelWithoutARowIsEnabledAndShared()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"enabled":true,"mode":"Shared"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheChannelWhenDisabledIsNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. SettingsPermissions, QuotationsPermissions.QuotationManage]);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"enabled":false,"mode":"Disabled"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheChannelWithoutQuotationManageIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheChannelForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.GetAsync(
            WhatsAppChannelUrl(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Criterio 5: la misma afirmación que la del comando, sobre el cuerpo HTTP.
    [Fact]
    public void TheRequestToStringDoesNotContainTheKey()
    {
        var request = new UpdateWhatsAppSettingsRequest("Own", "Zenvia", SentinelApiKey, FromNumber, TemplateId);

        Assert.DoesNotContain(SentinelApiKey, request.ToString(), StringComparison.Ordinal);
        Assert.Contains("***", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutWithoutBodyIsAValidationErrorOnMode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        using var request = new HttpRequestMessage(HttpMethod.Put, WhatsAppSettingsUrl(tenantId));
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "Mode",
            (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIsForbiddenOnThePutToo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, Guid.CreateVersion7(), new { mode = "Disabled" }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
