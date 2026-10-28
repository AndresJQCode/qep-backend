using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Api;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>Spec 2026-10-15 §4.3 y §7 por HTTP: el inicio A (tenant, permiso, módulo) y el B (anónimo, HMAC, limitador
/// <c>public</c>, sin el header de CSRF con el stub apagado), cada uno con su fila en
/// <c>integrations.shopify_installs</c>; el canje contra <see cref="FakeShopifyHandler"/> y la lectura de la instalación
/// con los tenants del usuario.</summary>
public sealed class ShopifyInstallApiTests
{
    private const string Timestamp = "1760796000";

    private const string Host = "YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvbWktdGllbmRh";

    // La constante de la ruta y no un literal: RequireCsrfHeaderMiddleware repite la ruta literal, y la prueba sin el
    // header de CSRF es la que comprueba que las dos coinciden.
    private const string AnonymousStartUrl = IntegrationsEndpoints.ShopifyInstallStartRoute;

    private const string CompleteUrl = IntegrationsEndpoints.ShopifyInstallCompleteRoute;

    private const string Shop = "mi-tienda.myshopify.com";

    private const string SentinelShopifyCode = "shopify-code-SENTINEL-5e6f";

    private const string SentinelShopifyAccessToken = "shpat_access-token-SENTINEL-7a8b";

    private static string InstallUrl(Guid installId) => $"/api/v1/integrations/shopify/installs/{installId}";

    private static string SignCallback(string code, string shop, string state, string timestamp = Timestamp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SentinelShopifyClientSecret),
            Encoding.UTF8.GetBytes($"code={code}&shop={shop}&state={state}&timestamp={timestamp}")));

    private static object CompleteBody(string state, string code = SentinelShopifyCode, string shop = Shop) =>
        new { code, shop, state, hmac = SignCallback(code, shop, state), timestamp = Timestamp, redirectUri = ShopifyRedirectUri };

    /// <summary>El inicio A de verdad, para tener una fila con su state.</summary>
    private static async Task<string> StartFromTenantAsync(HttpClient client, Guid tenantId)
    {
        var response = await SendAsync(client, HttpMethod.Post, TenantStartUrl(tenantId), new { shop = Shop, redirectUri = ShopifyRedirectUri });
        response.EnsureSuccessStatusCode();
        var authorize = await response.Content.ReadFromJsonAsync<ShopifyAuthorizeResponse>(Ct);
        return StateOf(authorize!.AuthorizeUrl);
    }

    /// <summary>Una membresía activa más del usuario, escrita directo, como <c>PipelineApiHarness.SeedMemberAsync</c>.</summary>
    private static Task<int> SeedMembershipAsync(string connectionString, Guid tenantId, Guid userId, string role) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            VALUES (@id, @userId, @tenantId, 'Active', ARRAY[@role]::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), NULL)
            """,
            ("id", Guid.CreateVersion7()),
            ("userId", userId),
            ("tenantId", tenantId),
            ("role", role));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TenantStartUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/shopify/install/start";

    private static string Sign(string shop, string timestamp = Timestamp, string host = Host) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SentinelShopifyClientSecret),
            Encoding.UTF8.GetBytes($"host={host}&shop={shop}&timestamp={timestamp}")));

    private static object AnonymousBody(string shop = "mi-tienda.myshopify.com", string? hmac = null) =>
        new { shop, hmac = hmac ?? Sign(shop), timestamp = Timestamp, host = Host, redirectUri = ShopifyRedirectUri };

    private static Task<long> CountInstallsAsync(string connectionString) =>
        ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.shopify_installs");

    private static string StateOf(string authorizeUrl) =>
        Uri.UnescapeDataString(new Uri(authorizeUrl).Query.TrimStart('?').Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);

    [Fact]
    public async Task StartAReturns200WithAuthorizeUrlAndStoresRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(
            client, HttpMethod.Post, TenantStartUrl(tenant.TenantId), new { shop = "MI-Tienda.myshopify.com", redirectUri = ShopifyRedirectUri });
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authorize = await response.Content.ReadFromJsonAsync<ShopifyAuthorizeResponse>(Ct);
        Assert.NotNull(authorize);
        Assert.StartsWith(
            $"https://mi-tienda.myshopify.com/admin/oauth/authorize?client_id={ShopifyClientId}&scope=read_orders%2Cread_customers&redirect_uri={Uri.EscapeDataString(ShopifyRedirectUri)}&state=",
            authorize.AuthorizeUrl,
            StringComparison.Ordinal);
        Assert.DoesNotContain("grant_options", authorize.AuthorizeUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        var state = StateOf(authorize.AuthorizeUrl);
        Assert.Equal(43, state.Length);

        var membership = await OwnerMembershipIdAsync(connectionString, tenant);
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM integrations.shopify_installs
            WHERE state = @state AND shop_domain = 'mi-tienda.myshopify.com' AND redirect_uri = @redirectUri
              AND initiated_tenant_id = @tenantId AND initiated_by = @membership
              AND expires_at - created_at = interval '15 minutes'
              AND access_token_protected IS NULL AND key_id IS NULL AND shop_id IS NULL
            """,
            ("state", state),
            ("redirectUri", ShopifyRedirectUri),
            ("tenantId", tenant.TenantId),
            ("membership", membership)));
    }

    [Fact]
    public async Task StartAWithoutPermissionReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var reader = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        using var manager = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var other = await RegisterTenantAsync(factory);
        var body = new { shop = "mi-tienda.myshopify.com", redirectUri = ShopifyRedirectUri };

        var withoutPermission = await SendAsync(reader, HttpMethod.Post, TenantStartUrl(tenant.TenantId), body);
        var otherTenant = await SendAsync(manager, HttpMethod.Post, TenantStartUrl(other.TenantId), body);
        await SetModuleStatusAsync(connectionString, tenant.TenantId, "shopify", "inactive");
        var moduleOff = await SendAsync(manager, HttpMethod.Post, TenantStartUrl(tenant.TenantId), body);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(moduleOff)).Code);
        Assert.Equal(0, await CountInstallsAsync(connectionString));
    }

    [Fact]
    public async Task StartAInvalidShopOrRedirectReturns422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var badShop = await SendAsync(client, HttpMethod.Post, TenantStartUrl(tenant.TenantId), new { shop = "evil.com", redirectUri = ShopifyRedirectUri });
        var badRedirect = await SendAsync(
            client, HttpMethod.Post, TenantStartUrl(tenant.TenantId), new { shop = "mi-tienda.myshopify.com", redirectUri = "https://evil.example/shopify/callback" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, badShop.StatusCode);
        var (shopCode, shopKeys) = await ProblemAsync(badShop);
        Assert.Equal("integrations.shopify.shop_invalid", shopCode);
        Assert.Equal(["shop"], shopKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRedirect.StatusCode);
        var (redirectCode, redirectKeys) = await ProblemAsync(badRedirect);
        Assert.Equal("validation.failed", redirectCode);
        Assert.Equal(["redirectUri"], redirectKeys);
        Assert.Equal(0, await CountInstallsAsync(connectionString));
    }

    [Fact]
    public async Task StartBValidHmacStoresRowWithoutTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();

        var response = await SendAsync(client, HttpMethod.Post, AnonymousStartUrl, AnonymousBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authorize = await response.Content.ReadFromJsonAsync<ShopifyAuthorizeResponse>(Ct);
        Assert.NotNull(authorize);
        Assert.StartsWith("https://mi-tienda.myshopify.com/admin/oauth/authorize?", authorize.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM integrations.shopify_installs
            WHERE state = @state AND shop_domain = 'mi-tienda.myshopify.com'
              AND initiated_tenant_id IS NULL AND initiated_by IS NULL
              AND expires_at - created_at = interval '15 minutes'
            """,
            ("state", StateOf(authorize.AuthorizeUrl))));
    }

    [Fact]
    public async Task StartBBadHmacReturns401()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();

        var response = await SendAsync(client, HttpMethod.Post, AnonymousStartUrl, AnonymousBody(hmac: new string('0', 64)));
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("integrations.shopify.hmac_invalid", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        Assert.Equal(0, await CountInstallsAsync(connectionString));
    }

    // Con el stub apagado, como el webhook: así existe RequireCsrfHeaderMiddleware. El inicio B no lleva sesión —lo abre
    // Shopify— y se autentica con su HMAC, así que pasa sin X-Qep-Client; el inicio A, que sí va con la cookie, no.
    [Fact]
    public async Task StartBIsAnonymousAndWithoutCsrfHeader()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var realAuth = factory.WithRealAuthentication();
        using var client = realAuth.CreateClient();

        var anonymous = await client.PostAsJsonAsync(AnonymousStartUrl, AnonymousBody(), Ct);
        var tenantRoute = await client.PostAsJsonAsync(
            TenantStartUrl(Guid.CreateVersion7()), new { shop = "mi-tienda.myshopify.com", redirectUri = ShopifyRedirectUri }, Ct);

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tenantRoute.StatusCode);
        Assert.Contains("X-Qep-Client", await tenantRoute.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(1, await CountInstallsAsync(connectionString));
    }

    [Fact]
    public async Task CompleteThenGetInstallListsUserTenantsWithFlags()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.ShopifyHandler.ScriptHappyInstall(Shop, SentinelShopifyAccessToken);
        var tenant = await RegisterTenantAsync(factory);
        var advisorTenant = await RegisterTenantAsync(factory);
        var moduleOffTenant = await RegisterTenantAsync(factory);
        await SeedMembershipAsync(connectionString, advisorTenant.TenantId, tenant.OwnerUserId, "advisor");
        await SeedMembershipAsync(connectionString, moduleOffTenant.TenantId, tenant.OwnerUserId, "admin");
        await SetModuleStatusAsync(connectionString, moduleOffTenant.TenantId, "shopify", "inactive");
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var anonymous = factory.CreateClient();

        var completed = await SendAsync(anonymous, HttpMethod.Post, CompleteUrl, CompleteBody(state));
        var completedText = await completed.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.DoesNotContain("SENTINEL", completedText, StringComparison.Ordinal);
        var install = await completed.Content.ReadFromJsonAsync<ShopifyInstallCompletedResponse>(Ct);
        Assert.NotNull(install);
        Assert.Equal(Shop, install.ShopDomain);
        Assert.Equal("Mi Tienda", install.ShopName);
        Assert.Equal(tenant.TenantId, install.InitiatedTenantId);
        Assert.Null(install.LinkedTenant);

        // La fila: token cifrado (nunca el texto), la tienda y +7 días desde el canje.
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM integrations.shopify_installs
            WHERE id = @id AND shop_id = '12345678' AND shop_name = 'Mi Tienda' AND scopes = 'read_orders,read_customers'
              AND access_token_protected IS NOT NULL AND key_id = 'test'
              AND expires_at > now() + interval '6 days 23 hours' AND expires_at <= now() + interval '7 days'
              AND position(convert_to(@token, 'UTF8') in access_token_protected) = 0
            """,
            ("id", install.InstallId),
            ("token", SentinelShopifyAccessToken)));

        // A Shopify: el canje con el par de la app y el code; la Admin API con el token recién canjeado.
        var requests = factory.ShopifyHandler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal($"https://{Shop}/admin/oauth/access_token", requests[0].Uri!.ToString());
        Assert.Contains(SentinelShopifyCode, requests[0].Body, StringComparison.Ordinal);
        Assert.Contains(SentinelShopifyClientSecret, requests[0].Body, StringComparison.Ordinal);
        Assert.Equal($"https://{Shop}/admin/api/2026-10/graphql.json", requests[1].Uri!.ToString());
        Assert.Equal(SentinelShopifyAccessToken, requests[1].AccessToken);

        var read = await owner.GetAsync(InstallUrl(install.InstallId), Ct);
        var readText = await read.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain("SENTINEL", readText, StringComparison.Ordinal);
        var view = await read.Content.ReadFromJsonAsync<ShopifyInstallResponse>(Ct);
        Assert.NotNull(view);
        Assert.Equal(Shop, view.ShopDomain);
        Assert.Equal("Mi Tienda", view.ShopName);
        Assert.Equal(install.ExpiresAt, view.ExpiresAt);
        Assert.Equal(tenant.TenantId, view.InitiatedTenantId);
        Assert.Equal(3, view.Tenants.Count);
        Assert.Equal((true, true), Flags(view, tenant.TenantId));
        Assert.Equal((false, true), Flags(view, advisorTenant.TenantId));
        Assert.Equal((true, false), Flags(view, moduleOffTenant.TenantId));
        Assert.All(view.Tenants, row => Assert.Equal("Integrations Test Org", row.Name));

        static (bool CanLink, bool ModuleEnabled) Flags(ShopifyInstallResponse view, Guid tenantId)
        {
            var row = view.Tenants.Single(candidate => candidate.TenantId == tenantId);
            return (row.CanLink, row.ModuleEnabled);
        }
    }

    [Fact]
    public async Task GetInstallUnknownReturns404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId);
        using var manager = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        // Una fila sin canjear: su id nunca salió del backend, así que tampoco existe para el GET.
        await StartFromTenantAsync(manager, tenant.TenantId);
        var notExchanged = await ScalarAsync<Guid>(connectionString, "SELECT id FROM integrations.shopify_installs");

        var unknown = await client.GetAsync(InstallUrl(Guid.NewGuid()), Ct);
        var pending = await client.GetAsync(InstallUrl(notExchanged), Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("integrations.shopify.install_not_found", (await ProblemAsync(unknown)).Code);
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        Assert.Equal("integrations.shopify.install_not_found", (await ProblemAsync(pending)).Code);
    }

    // Por HTTP: el doble clic del callback. El segundo no llama a Shopify ni toca la fila.
    [Fact]
    public async Task CompleteSameStateTwiceReturns404AndExchangesOnce()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.ShopifyHandler.ScriptHappyInstall(Shop, SentinelShopifyAccessToken);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var anonymous = factory.CreateClient();

        var first = await SendAsync(anonymous, HttpMethod.Post, CompleteUrl, CompleteBody(state));
        var second = await SendAsync(anonymous, HttpMethod.Post, CompleteUrl, CompleteBody(state));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("integrations.shopify.install_not_found", (await ProblemAsync(second)).Code);
        Assert.Single(factory.ShopifyHandler.Requests, request => request.Uri!.AbsolutePath == "/admin/oauth/access_token");
        Assert.Equal(1, await CountInstallsAsync(connectionString));
    }

    // Dos complete del mismo state a la vez. Shopify tarda en canjear, así que los dos leen la fila sin token; el
    // reclamo del state antes de llamar a Shopify deja pasar a uno solo, y el otro recibe install_not_found sin gastar el
    // code otra vez (RFC 6749 §4.1.2: un code canjeado dos veces puede revocar el token del primero).
    [Fact]
    public async Task CompleteConcurrentSameStateExchangesTheCodeOnce()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.ShopifyHandler.ScriptHappyInstall(Shop, SentinelShopifyAccessToken, exchangeDelay: TimeSpan.FromSeconds(1));
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();

        var responses = await Task.WhenAll(
            SendAsync(first, HttpMethod.Post, CompleteUrl, CompleteBody(state)),
            SendAsync(second, HttpMethod.Post, CompleteUrl, CompleteBody(state)));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.NotFound],
            responses.Select(response => response.StatusCode).Order().ToArray());
        var loser = responses.Single(response => response.StatusCode == HttpStatusCode.NotFound);
        Assert.Equal("integrations.shopify.install_not_found", (await ProblemAsync(loser)).Code);
        Assert.Single(factory.ShopifyHandler.Requests, request => request.Uri!.AbsolutePath == "/admin/oauth/access_token");
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.shopify_installs WHERE access_token_protected IS NOT NULL"));
    }

    // La rama perdedora del UPDATE condicionado, contra Postgres: dos lectores de la misma fila reclaman el mismo state y
    // sólo uno gana. La prueba HTTP de arriba puede cortarse antes, en la lectura; esta no.
    [Fact]
    public async Task ClaimSameStateTwiceOnlyOneWins()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IShopifyInstallRepository>();
        var first = await firstRepository.FindByStateAsync(state, Ct);
        var second = await secondRepository.FindByStateAsync(state, Ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        first.Claim($"{ShopifyInstall.ClaimedStatePrefix}first", DateTimeOffset.UtcNow);
        second.Claim($"{ShopifyInstall.ClaimedStatePrefix}second", DateTimeOffset.UtcNow);

        var firstWon = await firstRepository.TryClaimAsync(first, state, Ct);
        var secondWon = await secondRepository.TryClaimAsync(second, state, Ct);

        Assert.True(firstWon);
        Assert.False(secondWon);
        Assert.Equal(
            $"{ShopifyInstall.ClaimedStatePrefix}first",
            await ScalarAsync<string>(connectionString, "SELECT state FROM integrations.shopify_installs"));
        Assert.Null(await firstRepository.FindByStateAsync(state, Ct));
    }

    [Fact]
    public async Task CompleteRejectedByShopifyReturns422AndKeepsTheRowUnexchanged()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.ShopifyHandler.Respond("/admin/oauth/access_token$", HttpStatusCode.BadRequest, $$"""{"error":"invalid_request","error_description":"{{SentinelShopifyCode}}"}""");
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var anonymous = factory.CreateClient();

        var response = await SendAsync(anonymous, HttpMethod.Post, CompleteUrl, CompleteBody(state));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.shopify.code_exchange_failed", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain("SENTINEL", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        // El 422 queda en platform.request_failures: sin el code, ni el cuerpo de Shopify que lo repite.
        Assert.DoesNotContain("SENTINEL", await RequestFailuresTextAsync(connectionString), StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.shopify_installs WHERE access_token_protected IS NULL AND shop_id IS NULL"));
    }

    // El canje lo llama el SPA con fetch —Shopify sólo redirige el navegador a /shopify/callback, una página del
    // SPA— y el SPA manda X-Qep-Client siempre, así que no entra en la lista de exentos. Con el stub apagado: sin el
    // header, 403 de CSRF; con él, pasa sin sesión.
    [Fact]
    public async Task CompleteIsAnonymousButKeepsTheCsrfHeader()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        factory.ShopifyHandler.ScriptHappyInstall(Shop, SentinelShopifyAccessToken);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var state = await StartFromTenantAsync(owner, tenant.TenantId);
        using var realAuth = factory.WithRealAuthentication();
        using var client = realAuth.CreateClient();

        var withoutHeader = await client.PostAsJsonAsync(CompleteUrl, CompleteBody(state), Ct);
        var withHeader = await SendAsync(client, HttpMethod.Post, CompleteUrl, CompleteBody(state));

        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Contains("X-Qep-Client", await withoutHeader.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
    }
}
