using System.Net;
using System.Net.Http.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.1 por HTTP: 201 con la conexión Active y sus campos; la carrera por el
/// mismo número en dos tenants; el módulo apagado; el 422 de cada paso.</summary>
public sealed class EmbeddedSignupApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FinishCreatesAnActiveConnectionWithTheMetaFieldsAndItsRoute()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappySignup(factory.MetaHandler);
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        Assert.Equal("Active", connection.Status);
        Assert.Equal("+57 300 123 4567", connection.Fields["displayPhoneNumber"]);
        Assert.Equal("Origen Botánico", connection.Fields["verifiedName"]);
        Assert.Equal("111", connection.Fields["phoneNumberId"]);
        Assert.Equal("222", connection.Fields["wabaId"]);
        Assert.Equal("GREEN", connection.Fields["qualityRating"]);
        Assert.True(connection.Secrets["accessToken"].Configured);
        Assert.True(connection.Secrets["accessToken"].Readable);
        Assert.DoesNotContain(SentinelMetaAccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelMetaCode, body, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connection_routes WHERE external_id = '111' AND account_id = '222'"));
        var register = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/register", StringComparison.Ordinal));
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", register.Authorization);
        Assert.Matches("\"pin\":\"[0-9]{6}\"", register.Body);
        var exchange = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/oauth/access_token", StringComparison.Ordinal));
        Assert.Contains("client_id=" + MetaAppId, exchange.Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameNumberInTwoTenantsIsNumberAlreadyConnected()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappySignup(factory.MetaHandler);
        var first = await RegisterTenantAsync(factory);
        var second = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, first.TenantId, "messaging");
        await EnableModuleAsync(connectionString, second.TenantId, "messaging");
        using var client1 = CreateClient(factory, first.OwnerUserId, first.TenantId, ManagePermissions);
        using var client2 = CreateClient(factory, second.OwnerUserId, second.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client1, HttpMethod.Post, EmbeddedSignupUrl(first.TenantId), SignupBody())).StatusCode);
        var response = await SendAsync(client2, HttpMethod.Post, EmbeddedSignupUrl(second.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.whatsapp.number_already_connected", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, second.TenantId));
    }

    [Fact]
    public async Task WithTheModuleOffItIsForbiddenWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
        Assert.Empty(factory.MetaHandler.Requests);
    }

    [Theory]
    [InlineData("/oauth/access_token", 400, 100, "integrations.whatsapp.code_exchange_failed")]
    [InlineData("/111/register", 400, 133016, "integrations.whatsapp.registration_failed")]
    [InlineData("/222/subscribed_apps", 403, 10, "integrations.whatsapp.registration_failed")]
    public async Task AGraphFailureAnswers422WithItsCodeAndSavesNothing(string path, int status, int code, string expected)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.MetaHandler.Respond(path, (HttpStatusCode)status, FakeMetaGraphHandler.GraphError(code));
        ScriptHappySignup(factory.MetaHandler);
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expected, (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, tenant.TenantId));
    }

    [Fact]
    public async Task CoexistenceDoesNotRegisterAndUsesTheOnlyNumber()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.MetaHandler.Respond("/oauth/access_token", HttpStatusCode.OK, $$"""{"access_token":"{{SentinelMetaAccessToken}}"}""");
        factory.MetaHandler.Respond("/222/phone_numbers", HttpStatusCode.OK, """{"data":[{"id":"777","display_phone_number":"+57 7","verified_name":"Coex","quality_rating":"YELLOW"}]}""");
        factory.MetaHandler.Respond("/222/subscribed_apps", HttpStatusCode.OK, """{"success":true}""");
        factory.MetaHandler.Respond("/777\\?fields=", HttpStatusCode.OK, """{"display_phone_number":"+57 7","verified_name":"Coex","quality_rating":"YELLOW","id":"777"}""");
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, EmbeddedSignupUrl(tenant.TenantId), SignupBody(@event: "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING", phoneNumberId: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal("777", connection!.Fields["phoneNumberId"]);
        Assert.DoesNotContain(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal));
    }
}
