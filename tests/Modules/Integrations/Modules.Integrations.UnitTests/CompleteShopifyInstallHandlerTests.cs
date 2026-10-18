using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 paso 4, el canje: HMAC del callback, fila por <c>state</c> (un solo uso), misma tienda y mismo
/// <c>redirect_uri</c>, canje del <c>code</c>, scopes completos, lectura de la tienda y token cifrado con AAD
/// <c>installId</c> + <c>accessToken</c>; la fila pasa a vencer a los 7 días. Review Focus 5: el segundo canje del mismo
/// <c>state</c> es <c>install_not_found</c> sin volver a llamar a Shopify.
/// </summary>
public sealed class CompleteShopifyInstallHandlerTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string State = "Zm9vYmFyYmF6cXV4MDEyMzQ1Njc4OWFiY2RlZmdoaWpr";

    private const string Code = "shopify-code-SENTINEL-1a2b";

    private const string Timestamp = "1760796000";

    private const string RedirectUri = "https://qep.qcode.co/shopify/callback";

    private const string AccessToken = "shpat_access-token-SENTINEL-3c4d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Bed
    {
        public IntegrationsTestBed Integrations { get; } = new();

        public InMemoryShopifyInstallRepository Installs { get; } = new();

        public FakeShopifyAdminGateway Gateway { get; } = new();

        public FakeConnectionRoutes Routes { get; } = new();

        public FakeTenantDirectory Tenants { get; } = new();

        public CompleteShopifyInstallHandler Handler() =>
            new(
                Installs,
                Gateway,
                Integrations.ShopifyApp,
                Integrations.Protector,
                Routes,
                Tenants,
                Integrations.Clock,
                new CompleteShopifyInstallValidator());

        public ShopifyInstall Seed(Guid? tenantId = null, Guid? memberId = null, string state = State)
        {
            var install = ShopifyInstall.Start(Guid.NewGuid(), Shop, state, RedirectUri, tenantId, memberId, IntegrationsTestBed.Now);
            Installs.Add(install);
            return install;
        }

        public CompleteShopifyInstallCommand Command(
            string shop = Shop, string state = State, string redirectUri = RedirectUri, string? hmac = null, string code = Code) =>
            new(
                code,
                shop,
                state,
                hmac ?? Sign(Integrations.ShopifyApp.ClientSecret!, ("code", code), ("shop", shop), ("state", state), ("timestamp", Timestamp)),
                Timestamp,
                redirectUri);
    }

    private static string Sign(string secret, params (string Key, string Value)[] parameters) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(string.Join('&', parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")))));

    [Fact]
    public async Task CompleteUnknownStateThrowsInstallNotFound()
    {
        var bed = new Bed();
        bed.Seed(state: "otro-state-0123456789abcdefghijklmnopqrstuv");

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task CompleteExpiredRowThrowsInstallExpired()
    {
        var bed = new Bed();
        bed.Seed();
        bed.Integrations.Clock.UtcNow = IntegrationsTestBed.Now.AddMinutes(16);

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallExpired, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    // La firma es válida para otra tienda, pero el state es de mi-tienda: la fila no es de esa tienda.
    [Fact]
    public async Task CompleteShopMismatchThrowsInstallNotFound()
    {
        var bed = new Bed();
        bed.Seed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.Handler().HandleAsync(bed.Command(shop: "otra-tienda.myshopify.com"), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    // §12: el redirect_uri del canje tiene que ser idéntico al de la autorización.
    [Fact]
    public async Task CompleteRedirectMismatchThrowsInstallNotFound()
    {
        var bed = new Bed();
        bed.Seed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.Handler().HandleAsync(bed.Command(redirectUri: "http://localhost:3002/shopify/callback"), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task CompleteBadHmacThrowsHmacInvalid()
    {
        var bed = new Bed();
        bed.Seed();

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            bed.Handler().HandleAsync(bed.Command(hmac: new string('0', 64)), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyHmacInvalid, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    // Shopify agrega host al callback; si el SPA lo reenvía, entra en la firma.
    [Fact]
    public async Task CompleteSignedWithHostVerifies()
    {
        var bed = new Bed();
        bed.Seed();
        const string host = "YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvbWktdGllbmRh";
        var hmac = Sign(bed.Integrations.ShopifyApp.ClientSecret!, ("code", Code), ("host", host), ("shop", Shop), ("state", State), ("timestamp", Timestamp));

        var response = await bed.Handler().HandleAsync(bed.Command(hmac: hmac) with { Host = host }, Ct);

        Assert.Equal(Shop, response.ShopDomain);
    }

    [Fact]
    public async Task CompleteMissingCodeThrowsValidation()
    {
        var bed = new Bed();
        bed.Seed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Handler().HandleAsync(bed.Command() with { Code = " " }, Ct));

        Assert.Contains("code", error.Errors.Select(failure => failure.PropertyName));
    }

    [Fact]
    public async Task CompleteScopesMissingThrows()
    {
        var bed = new Bed();
        var install = bed.Seed();
        bed.Gateway.GrantedScopes = "read_orders";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyScopesMissing, error.Code);
        Assert.Equal(["exchange"], bed.Gateway.Calls);
        Assert.Empty(bed.Installs.Exchanged);
        Assert.False(install.IsExchanged);
    }

    // Shopify no repite read_x cuando concede write_x: write implica read.
    [Fact]
    public async Task CompleteWriteScopeSatisfiesTheReadOne()
    {
        var bed = new Bed();
        bed.Seed();
        bed.Gateway.GrantedScopes = "write_orders,read_customers";

        var response = await bed.Handler().HandleAsync(bed.Command(), Ct);

        Assert.Equal(Shop, response.ShopDomain);
    }

    [Theory]
    [InlineData(ShopifyGatewayFailure.Rejected)]
    [InlineData(ShopifyGatewayFailure.Unreachable)]
    [InlineData(ShopifyGatewayFailure.Invalid)]
    public async Task CompleteExchangeRejectedThrowsCodeExchangeFailed(ShopifyGatewayFailure failure)
    {
        var bed = new Bed();
        bed.Seed();
        bed.Gateway.FailAt = "exchange";
        bed.Gateway.Failure = failure;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyCodeExchangeFailed, error.Code);
        Assert.Empty(bed.Installs.Exchanged);
    }

    // El code ya se gastó: no hay forma de reintentar la lectura sin otro, así que es el mismo error que el canje.
    [Fact]
    public async Task CompleteShopReadFailsThrowsCodeExchangeFailed()
    {
        var bed = new Bed();
        bed.Seed();
        bed.Gateway.FailAt = "shop";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyCodeExchangeFailed, error.Code);
        Assert.Equal(["exchange", "shop"], bed.Gateway.Calls);
        Assert.Empty(bed.Installs.Exchanged);
    }

    // Sin llave no se puede guardar el token: 503 antes de gastar el code en Shopify.
    [Fact]
    public async Task CompleteWithoutActiveKeyThrows503BeforeExchanging()
    {
        var bed = new Bed();
        bed.Seed();
        bed.Integrations.Protector.ActiveKeyId = null;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.SecretProtectionUnavailable, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task CompleteSuccessStoresProtectedTokenAndExtendsSevenDays()
    {
        var bed = new Bed();
        var install = bed.Seed();
        bed.Integrations.Clock.UtcNow = IntegrationsTestBed.Now.AddMinutes(5);
        var command = bed.Command();

        var response = await bed.Handler().HandleAsync(command, Ct);

        var stored = Assert.Single(bed.Installs.Exchanged.Values);
        Assert.Equal(install.Id, stored.Id);
        Assert.Equal("12345678", stored.ShopId);
        Assert.Equal("Mi Tienda", stored.ShopName);
        Assert.Equal("read_orders,read_customers", stored.Scopes);
        Assert.Equal(IntegrationsTestBed.Now.AddMinutes(5).AddDays(7), stored.ExpiresAt);
        Assert.Equal(
            AccessToken,
            bed.Integrations.Protector.Unprotect(install.Id, ShopifyFieldKeys.AccessToken, new ProtectedSecret(stored.KeyId!, stored.ProtectedToken!)));
        Assert.Equal((Shop, AccessToken), bed.Gateway.LastShopRead);

        Assert.Equal(install.Id, response.InstallId);
        Assert.Equal(Shop, response.ShopDomain);
        Assert.Equal("Mi Tienda", response.ShopName);
        Assert.Equal(stored.ExpiresAt, response.ExpiresAt);
        Assert.Null(response.InitiatedTenantId);
        Assert.Null(response.LinkedTenant);

        Assert.DoesNotContain(AccessToken, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Code, command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, new ShopifyTokenGrant(AccessToken, "read_orders").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteFromTenantReturnsInitiatedTenantId()
    {
        var bed = new Bed();
        bed.Seed(bed.Integrations.TenantId, bed.Integrations.MemberId);

        var response = await bed.Handler().HandleAsync(bed.Command(), Ct);

        Assert.Equal(bed.Integrations.TenantId, response.InitiatedTenantId);
    }

    [Fact]
    public async Task CompleteSameStateTwiceSecondReturnsInstallNotFound()
    {
        var bed = new Bed();
        bed.Seed();
        await bed.Handler().HandleAsync(bed.Command(), Ct);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
        Assert.Equal(1, bed.Gateway.Calls.Count(call => call == "exchange"));
        Assert.Single(bed.Installs.Added);
    }

    // Los dos clics leyeron la fila sin token; el UPDATE condicionado deja ganar a uno solo.
    [Fact]
    public async Task CompleteLosingTheRaceThrowsInstallNotFound()
    {
        var bed = new Bed();
        bed.Seed();
        bed.Installs.LoseNextExchange = true;

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.Handler().HandleAsync(bed.Command(), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyInstallNotFound, error.Code);
        Assert.Empty(bed.Installs.Exchanged);
    }

    [Fact]
    public async Task CompleteExistingRouteReturnsLinkedTenant()
    {
        var bed = new Bed();
        bed.Seed();
        var otherTenant = Guid.NewGuid();
        bed.Routes.Routes[(IntegrationProviders.Shopify.Key, Shop)] = new ConnectionRoute(otherTenant, Guid.NewGuid(), ConnectionStatus.Active);
        bed.Tenants.Names[otherTenant] = "Distribuidora Andina";

        var response = await bed.Handler().HandleAsync(bed.Command(), Ct);

        Assert.Equal(new LinkedTenantResponse(otherTenant, "Distribuidora Andina"), response.LinkedTenant);
    }
}

/// <summary>Shopify de mentira para el handler: una lista de llamadas, la falla dónde se pida y los valores por defecto
/// de una tienda sana.</summary>
internal sealed class FakeShopifyAdminGateway : IShopifyAdminGateway
{
    public List<string> Calls { get; } = [];

    public string? FailAt { get; set; }

    public ShopifyGatewayFailure Failure { get; set; } = ShopifyGatewayFailure.Rejected;

    public string GrantedScopes { get; set; } = "read_orders,read_customers";

    public (string Shop, string Token)? LastShopRead { get; private set; }

    public Task<ShopifyTokenGrant> ExchangeCodeAsync(string shop, string code, CancellationToken cancellationToken)
    {
        Calls.Add("exchange");
        ThrowIfFailing("exchange");
        return Task.FromResult(new ShopifyTokenGrant("shpat_access-token-SENTINEL-3c4d", GrantedScopes));
    }

    public Task<ShopifyShopInfo> GetShopAsync(string shop, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add("shop");
        ThrowIfFailing("shop");
        LastShopRead = (shop, accessToken);
        return Task.FromResult(new ShopifyShopInfo("12345678", "Mi Tienda", shop));
    }

    public Task<IReadOnlyList<ShopifyWebhookSubscription>> ListWebhooksAsync(string shop, string token, CancellationToken cancellationToken)
    {
        Calls.Add("list-webhooks");
        ThrowIfFailing("list-webhooks");
        return Task.FromResult<IReadOnlyList<ShopifyWebhookSubscription>>([]);
    }

    public Task<ShopifyWebhookSubscription> CreateWebhookAsync(
        string shop, string token, string topic, Uri callback, CancellationToken cancellationToken)
    {
        Calls.Add($"create-webhook:{topic}");
        ThrowIfFailing("create-webhook");
        return Task.FromResult(new ShopifyWebhookSubscription($"gid://shopify/WebhookSubscription/{Calls.Count}", topic, callback));
    }

    private void ThrowIfFailing(string step)
    {
        if (string.Equals(FailAt, step, StringComparison.Ordinal))
        {
            throw new ShopifyGatewayException(Failure, Failure == ShopifyGatewayFailure.Rejected ? 401 : null);
        }
    }
}

internal sealed class FakeConnectionRoutes : IConnectionRoutes
{
    public Dictionary<(string ProviderKey, string ExternalId), ConnectionRoute> Routes { get; } = [];

    public Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(Routes.TryGetValue((providerKey, externalId), out var route) ? route : null);

    public Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectionRoute>>([]);
}

/// <summary>Sólo el nombre: es lo único que la instalación lee de un tenant.</summary>
internal sealed class FakeTenantDirectory : ITenantDirectory
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Names.TryGetValue(tenantId.Value, out var name) ? name : null);

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<TenantStatus?>(null);

    public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantRegionalSettings?>(null);
}
