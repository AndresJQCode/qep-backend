using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Meta;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>Spec 2026-10-14 §5.1 y §8.1 por HTTP: el catálogo con <c>instagram</c> y su onboarding; el 201 con la
/// conexión Active, sus campos, su ruta y las llamadas a Meta en orden; la cuenta en dos tenants; los permisos que
/// faltan; el módulo apagado; el origen de retorno ajeno; el probador; y el worker que refresca el token.</summary>
public sealed class InstagramConnectApiTests
{
    private const string IgUserId = "17841400000000001";
    private const string SentinelCode = "AQB-instagram-code-SENTINEL";
    private const string SentinelShortToken = "ig-short-token-SENTINEL-1a";
    private const string SentinelLongToken = "ig-long-token-SENTINEL-2b";
    private const string SentinelRefreshedToken = "ig-refreshed-token-SENTINEL-3c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void ScriptHappyConnect(FakeMetaGraphHandler graph, string permissions = "instagram_business_basic,instagram_business_manage_messages", string accountType = "BUSINESS")
    {
        graph.Respond("^/oauth/access_token", HttpStatusCode.OK,
            $$$"""{"data":[{"access_token":"{{{SentinelShortToken}}}","user_id":{{{IgUserId}}},"permissions":"{{{permissions}}}"}]}""");
        graph.Respond(@"^/access_token\?grant_type=ig_exchange_token", HttpStatusCode.OK,
            $$$"""{"access_token":"{{{SentinelLongToken}}}","token_type":"bearer","expires_in":5184000}""");
        graph.Respond(@"^/refresh_access_token\?grant_type=ig_refresh_token", HttpStatusCode.OK,
            $$$"""{"access_token":"{{{SentinelRefreshedToken}}}","token_type":"bearer","expires_in":5184000}""");
        graph.Respond(@"^/v24\.0/me\?", HttpStatusCode.OK,
            $$$"""{"id":"{{{IgUserId}}}","username":"origenbotanico","name":"Origen Botánico","account_type":"{{{accountType}}}"}""");
        graph.Respond($@"^/v24\.0/{IgUserId}/subscribed_apps", HttpStatusCode.OK, """{"success":true}""");
    }

    private static object ConnectBody(string name = "Instagram Ventas", string redirectUri = InstagramRedirectUri) =>
        new { name, code = SentinelCode, redirectUri };

    private static async Task<(RegisteredTenant Tenant, HttpClient Client)> TenantWithMessagingAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        return (tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    [Fact]
    public async Task TheCatalogShowsInstagramWithItsOnboardingOnlyWithTheInstagramApp()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, _) = await TenantWithMessagingAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var json = await client.GetStringAsync(CatalogUrl(tenant.TenantId), Ct);
        using var document = JsonDocument.Parse(json);
        var instagram = document.RootElement.GetProperty("providers").EnumerateArray()
            .Single(provider => provider.GetProperty("key").GetString() == "instagram");
        var onboarding = instagram.GetProperty("onboarding");
        Assert.Equal("InstagramLogin", onboarding.GetProperty("kind").GetString());
        Assert.Equal(InstagramAppId, onboarding.GetProperty("appId").GetString());
        Assert.Equal("https://www.instagram.com/oauth/authorize", onboarding.GetProperty("authorizeUrl").GetString());
        Assert.Equal(["instagram_business_basic", "instagram_business_manage_messages"], onboarding.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()));
        Assert.False(onboarding.TryGetProperty("configId", out _));
        Assert.Equal(["username", "accountName"], instagram.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("key").GetString()));
        Assert.DoesNotContain(SentinelInstagramAppSecret, json, StringComparison.Ordinal);

        using var withoutInstagram = factory.WithoutMetaInstagram();
        using var client2 = CreateClient(withoutInstagram, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var json2 = await client2.GetStringAsync(CatalogUrl(tenant.TenantId), Ct);
        Assert.DoesNotContain("\"instagram\"", json2, StringComparison.Ordinal);
        Assert.Contains("whatsapp-cloud", json2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectCreatesAnActiveConnectionWithTheAccountFieldsItsRouteAndTheMetaCallsInOrder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;

        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        Assert.Equal("instagram", connection.ProviderKey);
        Assert.Equal("Active", connection.Status);
        Assert.Equal("origenbotanico", connection.Fields["username"]);
        Assert.Equal("Origen Botánico", connection.Fields["accountName"]);
        // Los internos salen en la conexión (no en el catálogo), como phoneNumberId en WhatsApp.
        Assert.Equal(IgUserId, connection.Fields["igUserId"]);
        Assert.True(DateTimeOffset.TryParse(connection.Fields["tokenExpiresAt"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt));
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddDays(59), DateTimeOffset.UtcNow.AddDays(61));
        Assert.True(connection.Secrets["accessToken"].Configured);
        Assert.True(connection.Secrets["accessToken"].Readable);
        Assert.DoesNotContain(SentinelCode, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelShortToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelLongToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelInstagramAppSecret, body, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM integrations.connection_routes WHERE provider_key = 'instagram' AND external_id = '{IgUserId}' AND account_id IS NULL"));

        var requests = factory.InstagramHandler.Requests.ToArray();
        Assert.Equal(
            ["/oauth/access_token", "/access_token", "/v24.0/me", $"/v24.0/{IgUserId}/subscribed_apps"],
            requests.Select(request => request.Uri!.AbsolutePath));
        var exchange = requests[0];
        Assert.Equal(HttpMethod.Post, exchange.Method);
        Assert.Equal("api.instagram.com", exchange.Uri!.Host);
        Assert.Contains($"client_id={InstagramAppId}", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("grant_type=authorization_code", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(InstagramRedirectUri), exchange.Body, StringComparison.Ordinal);
        Assert.Null(exchange.Authorization);
        Assert.Equal("graph.instagram.com", requests[1].Uri!.Host);
        Assert.Contains($"access_token={SentinelShortToken}", requests[1].Uri!.Query, StringComparison.Ordinal);
        Assert.Equal($"Bearer {SentinelLongToken}", requests[2].Authorization);
        Assert.Equal(HttpMethod.Post, requests[3].Method);
        Assert.Contains("subscribed_fields=messages,messaging_seen,messaging_postbacks,messaging_reactions", requests[3].Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameAccountInTwoTenantsIsAccountAlreadyConnected()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var (first, firstClient) = await TenantWithMessagingAsync(factory, connectionString);
        var (second, secondClient) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = firstClient;
        using var __ = secondClient;

        var created = await SendAsync(firstClient, HttpMethod.Post, InstagramConnectUrl(first.TenantId), ConnectBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var again = await SendAsync(secondClient, HttpMethod.Post, InstagramConnectUrl(second.TenantId), ConnectBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var (code, _) = await ProblemAsync(again);
        Assert.Equal("integrations.instagram.account_already_connected", code);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connections WHERE tenant_id = @tenantId", ("tenantId", second.TenantId)));
    }

    [Fact]
    public async Task MissingPermissionsArePermissionsMissingAndNothingIsSaved()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler, permissions: "instagram_business_basic");
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;

        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, _) = await ProblemAsync(response);
        Assert.Equal("integrations.instagram.permissions_missing", code);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connections"));
        Assert.Single(factory.InstagramHandler.Requests);
    }

    [Fact]
    public async Task APersonalAccountIsAccountNotProfessional()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler, accountType: "PERSONAL");
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;

        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.instagram.account_not_professional", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ARejectedCodeIsCodeExchangeFailed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.InstagramHandler.Respond("^/oauth/access_token", HttpStatusCode.BadRequest, """{"error_type":"OAuthException","code":400,"error_message":"Invalid authorization code"}""");
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;

        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.instagram.code_exchange_failed", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ARedirectUriFromAnotherOriginIsAValidationFailureOnThatField()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;

        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody(redirectUri: "https://evil.example/callback"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["redirectUri"], errorKeys);
        Assert.Empty(factory.InstagramHandler.Requests);
    }

    [Fact]
    public async Task WithMessagingOffTheProviderIsHiddenAndConnectIsModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var catalog = await client.GetStringAsync(CatalogUrl(tenant.TenantId), Ct);
        Assert.DoesNotContain("\"instagram\"", catalog, StringComparison.Ordinal);
        var response = await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
        Assert.Empty(factory.InstagramHandler.Requests);
    }

    // El probador de instagram: /me con el token guardado refresca el usuario; el formulario sólo puede cambiar el nombre.
    [Fact]
    public async Task TestRefreshesTheUsernameAndTheFieldsAreBackendOwned()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;
        var created = await (await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody())).Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        factory.InstagramHandler.Reset();
        factory.InstagramHandler.Requests.Clear();
        factory.InstagramHandler.Respond(@"^/v24\.0/me\?", HttpStatusCode.OK, $$$"""{"id":"{{{IgUserId}}}","username":"origen.botanico","name":"Origen Botánico"}""");

        var tested = await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, created!.Id)}/test");

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var refreshed = await tested.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal("origen.botanico", refreshed!.Fields["username"]);
        var test = Assert.Single(factory.InstagramHandler.Requests);
        Assert.Equal($"Bearer {SentinelLongToken}", test.Authorization);

        var edited = await SendAsync(client, HttpMethod.Put, ConnectionUrl(tenant.TenantId, created.Id),
            new { name = "Ventas", fields = new Dictionary<string, string?> { ["username"] = "otro" }, secrets = new Dictionary<string, string?>() },
            ifMatch: $"\"{refreshed.Version}\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edited.StatusCode);
        Assert.Equal(["fields.username"], (await ProblemAsync(edited)).ErrorKeys);
    }

    // Spec 2026-10-14, decisión 5: el token con menos de 15 días y más de 24 h se refresca; un 4xx de Meta deja la
    // conexión en NeedsAttention con token_expired.
    [Fact]
    public async Task TheWorkerRefreshesADueTokenAndMarksARejectedOneAsNeedsAttention()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ScriptHappyConnect(factory.InstagramHandler);
        var (tenant, client) = await TenantWithMessagingAsync(factory, connectionString);
        using var _ = client;
        var created = await (await SendAsync(client, HttpMethod.Post, InstagramConnectUrl(tenant.TenantId), ConnectBody())).Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        var worker = factory.Services.GetServices<IHostedService>().OfType<InstagramTokenRefreshWorker>().Single();
        await worker.FirstRunCompletion;
        factory.InstagramHandler.Requests.Clear();

        // Fresco y lejos de vencer: nada que hacer.
        var idle = await worker.RunOnceAsync(Ct);
        Assert.Equal((0, 0, 0), (idle.Refreshed, idle.Rejected, idle.Deferred));
        Assert.Empty(factory.InstagramHandler.Requests);

        // A 10 días de vencer pero con menos de 24 h: se difiere (Meta no refresca un token nuevo).
        await ExecuteAsync(connectionString,
            "UPDATE integrations.connections SET fields = jsonb_set(fields, '{tokenExpiresAt}', to_jsonb((now() + interval '10 days')::text)) WHERE id = @id",
            ("id", created!.Id));
        var young = await worker.RunOnceAsync(Ct);
        Assert.Equal((0, 0, 1), (young.Refreshed, young.Rejected, young.Deferred));

        // Con más de 24 h: se refresca, el secreto nuevo descifra y la fecha se corre.
        await ExecuteAsync(connectionString, "UPDATE integrations.connection_secrets SET updated_at = now() - interval '2 days' WHERE connection_id = @id", ("id", created.Id));
        var refreshed = await worker.RunOnceAsync(Ct);
        Assert.Equal((1, 0, 0), (refreshed.Refreshed, refreshed.Rejected, refreshed.Deferred));
        var refresh = Assert.Single(factory.InstagramHandler.Requests);
        Assert.Equal("/refresh_access_token", refresh.Uri!.AbsolutePath);
        Assert.Contains($"access_token={SentinelLongToken}", refresh.Uri.Query, StringComparison.Ordinal);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var resolved = await scope.ServiceProvider.GetRequiredService<IIntegrationConnections>().ResolveAsync(tenant.TenantId, created.Id, Ct);
            Assert.Equal(SentinelRefreshedToken, resolved!.Secrets["accessToken"]);
            Assert.True(DateTimeOffset.Parse(resolved.Fields["tokenExpiresAt"], null, System.Globalization.DateTimeStyles.RoundtripKind) > DateTimeOffset.UtcNow.AddDays(59));
        }

        // Meta rechaza el refresco: NeedsAttention con token_expired.
        factory.InstagramHandler.Reset();
        factory.InstagramHandler.Respond(@"^/refresh_access_token", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(190));
        await ExecuteAsync(connectionString,
            "UPDATE integrations.connections SET fields = jsonb_set(fields, '{tokenExpiresAt}', to_jsonb((now() + interval '3 days')::text)) WHERE id = @id",
            ("id", created.Id));
        await ExecuteAsync(connectionString, "UPDATE integrations.connection_secrets SET updated_at = now() - interval '2 days' WHERE connection_id = @id", ("id", created.Id));
        var rejected = await worker.RunOnceAsync(Ct);
        Assert.Equal((0, 1, 0), (rejected.Refreshed, rejected.Rejected, rejected.Deferred));
        var back = await client.GetFromJsonAsync<ConnectionResponse>(ConnectionUrl(tenant.TenantId, created.Id), Ct);
        Assert.Equal("NeedsAttention", back!.Status);
        Assert.Equal("token_expired", back.LastFailureCode);
    }
}
