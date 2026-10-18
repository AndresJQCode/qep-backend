using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Modules.Integrations.Api;
using Modules.Integrations.Application.Shopify;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>Spec 2026-10-15 §4.3 y §7 por HTTP: el inicio A (tenant, permiso, módulo) y el B (anónimo, HMAC, limitador
/// <c>public</c>, sin el header de CSRF con el stub apagado), cada uno con su fila en
/// <c>integrations.shopify_installs</c>.</summary>
public sealed class ShopifyInstallApiTests
{
    private const string Timestamp = "1760796000";

    private const string Host = "YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvbWktdGllbmRh";

    // La constante de la ruta y no un literal: RequireCsrfHeaderMiddleware repite la ruta literal, y la prueba sin el
    // header de CSRF es la que comprueba que las dos coinciden.
    private const string AnonymousStartUrl = IntegrationsEndpoints.ShopifyInstallStartRoute;

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
}
