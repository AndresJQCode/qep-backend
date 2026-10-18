using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 (A1, B1-B2): los dos inicios de la instalación. El A exige permiso, tenant y módulo
/// <c>shopify</c>; el B, el HMAC de la query de Shopify. Los dos validan el dominio y el origen del retorno, guardan
/// una fila con un <c>state</c> de 32 bytes que vence a los 15 minutos y devuelven la URL de autorización sin
/// <c>grant_options[]=per-user</c> (el token tiene que ser offline).
/// </summary>
public sealed partial class StartShopifyInstallHandlerTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string RedirectUri = "https://qep.qcode.co/shopify/callback";

    private const string Timestamp = "1760796000";

    private const string Host = "YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvbWktdGllbmRh";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Base64UrlState();

    private sealed class Bed
    {
        public IntegrationsTestBed Integrations { get; } = new();

        public InMemoryShopifyInstallRepository Installs { get; } = new();

        public StartShopifyInstallForTenantHandler ForTenant(IExecutionContext? context = null) =>
            new(
                Installs,
                Integrations.UnitOfWork,
                Integrations.ShopifyApp,
                Integrations.Modules,
                Integrations.Memberships,
                context ?? Integrations.Context(),
                Integrations.Clock,
                new StartShopifyInstallForTenantValidator());

        public StartShopifyInstallHandler Anonymous() =>
            new(Installs, Integrations.UnitOfWork, Integrations.ShopifyApp, Integrations.Clock, new StartShopifyInstallValidator());
    }

    private static string Sign(string secret, params (string Key, string Value)[] parameters) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(string.Join('&', parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")))));

    // Sin Microsoft.AspNetCore.WebUtilities en este proyecto: la query de la URL de autorización, a mano.
    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);

    private static StartShopifyInstallCommand Anonymous(Bed bed, string? hmac = null, string shop = Shop, string redirectUri = RedirectUri) =>
        new(shop, hmac ?? Sign(bed.Integrations.ShopifyApp.ClientSecret!, ("host", Host), ("shop", shop), ("timestamp", Timestamp)), Timestamp, Host, redirectUri);

    [Fact]
    public async Task StartForTenantModuleOffThrows403()
    {
        var bed = new Bed();
        bed.Integrations.Modules.Sets[bed.Integrations.TenantId] =
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Shopify]));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, RedirectUri), Ct));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
        Assert.Empty(bed.Installs.Added);
        Assert.Equal(0, bed.Integrations.UnitOfWork.Saves);
    }

    [Fact]
    public async Task StartForTenantWithoutManagePermissionThrows403()
    {
        var bed = new Bed();
        bed.Integrations.Permissions = [IntegrationsPermissions.ConnectionRead];

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, RedirectUri), Ct));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(bed.Installs.Added);
    }

    [Fact]
    public async Task StartForTenantOtherTenantThrows403()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(Guid.CreateVersion7(), Shop, RedirectUri), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    // D-M3: sin el par de la app (sólo fuera de Production) shopify no sale en el catálogo; iniciar responde lo mismo
    // que un proveedor oculto.
    [Fact]
    public async Task StartForTenantAppNotConfiguredThrows403()
    {
        var bed = new Bed();
        bed.Integrations.ShopifyApp.Configured = false;

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, RedirectUri), Ct));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
    }

    [Fact]
    public async Task StartForTenantRedirectNotAllowedThrowsValidation()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.ForTenant().HandleAsync(
                new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, "https://evil.example/shopify/callback"), Ct));

        Assert.Equal(["redirectUri"], error.Errors.Select(failure => failure.PropertyName).Distinct());
        Assert.Empty(bed.Installs.Added);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://qep.qcode.co/shopify/callback")]
    public async Task StartForTenantMalformedRedirectThrowsValidation(string redirectUri)
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, redirectUri), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "redirectUri");
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("mi-tienda.myshopify.com.evil.com")]
    public async Task StartForTenantInvalidShopThrowsShopInvalid(string shop)
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ForTenant().HandleAsync(new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, shop, RedirectUri), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyShopInvalid, error.Code);
        Assert.Equal(["shop"], error.FieldErrors.Keys);
        Assert.Empty(bed.Installs.Added);
    }

    [Fact]
    public async Task StartForTenantBuildsAuthorizeUrlWithoutPerUser()
    {
        var bed = new Bed();

        var response = await bed.ForTenant().HandleAsync(
            new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, "MI-Tienda.myshopify.com", RedirectUri), Ct);

        var url = new Uri(response.AuthorizeUrl);
        Assert.Equal("https", url.Scheme);
        Assert.Equal(Shop, url.Host);
        Assert.Equal("/admin/oauth/authorize", url.AbsolutePath);
        var query = ParseQuery(url.Query);
        Assert.Equal(["client_id", "redirect_uri", "scope", "state"], query.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("grant_options", response.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(bed.Integrations.ShopifyApp.ClientId, query["client_id"]);
        Assert.Equal("read_orders,read_customers", query["scope"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        var state = query["state"];
        Assert.Matches(Base64UrlState(), state);

        var install = Assert.Single(bed.Installs.Added);
        Assert.Equal(Shop, install.ShopDomain);
        Assert.Equal(state, install.State);
        Assert.Equal(RedirectUri, install.RedirectUri);
        Assert.Equal(bed.Integrations.TenantId, install.InitiatedTenantId);
        Assert.Equal(bed.Integrations.MemberId, install.InitiatedBy);
        Assert.Equal(IntegrationsTestBed.Now, install.CreatedAt);
        Assert.Equal(IntegrationsTestBed.Now.AddMinutes(15), install.ExpiresAt);
        Assert.False(install.IsExchanged);
        Assert.Equal(1, bed.Integrations.UnitOfWork.Saves);
        // El secreto de la app nunca va en la URL.
        Assert.DoesNotContain("SENTINEL", response.AuthorizeUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartForTenantEachCallHasItsOwnState()
    {
        var bed = new Bed();
        var command = new StartShopifyInstallForTenantCommand(bed.Integrations.TenantId, Shop, RedirectUri);

        await bed.ForTenant().HandleAsync(command, Ct);
        await bed.ForTenant().HandleAsync(command, Ct);

        Assert.Equal(2, bed.Installs.Added.Select(install => install.State).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task StartAnonymousBadHmacThrows()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            bed.Anonymous().HandleAsync(Anonymous(bed, hmac: new string('0', 64)), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyHmacInvalid, error.Code);
        Assert.Empty(bed.Installs.Added);
        Assert.Equal(0, bed.Integrations.UnitOfWork.Saves);
    }

    [Fact]
    public async Task StartAnonymousHmacSignedWithOtherSecretThrows()
    {
        var bed = new Bed();
        var hmac = Sign("otro-secreto", ("host", Host), ("shop", Shop), ("timestamp", Timestamp));

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => bed.Anonymous().HandleAsync(Anonymous(bed, hmac), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyHmacInvalid, error.Code);
    }

    // Sin la app configurada no hay con qué verificar la firma: es un HMAC inválido, no un 500.
    [Fact]
    public async Task StartAnonymousAppNotConfiguredThrowsHmacInvalid()
    {
        var bed = new Bed();
        var command = Anonymous(bed);
        bed.Integrations.ShopifyApp.Configured = false;

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => bed.Anonymous().HandleAsync(command, Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyHmacInvalid, error.Code);
    }

    [Fact]
    public async Task StartAnonymousValidHmacStoresRowWithoutTenant()
    {
        var bed = new Bed();

        var response = await bed.Anonymous().HandleAsync(Anonymous(bed), Ct);

        var install = Assert.Single(bed.Installs.Added);
        Assert.Equal(Shop, install.ShopDomain);
        Assert.Null(install.InitiatedTenantId);
        Assert.Null(install.InitiatedBy);
        Assert.Equal(IntegrationsTestBed.Now.AddMinutes(15), install.ExpiresAt);
        var query = ParseQuery(new Uri(response.AuthorizeUrl).Query);
        Assert.Equal(install.State, query["state"]);
        Assert.DoesNotContain("grant_options", response.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(1, bed.Integrations.UnitOfWork.Saves);
    }

    [Fact]
    public async Task StartAnonymousRedirectNotAllowedThrowsValidation()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Anonymous().HandleAsync(Anonymous(bed, redirectUri: "https://evil.example/shopify/callback"), Ct));

        Assert.Equal(["redirectUri"], error.Errors.Select(failure => failure.PropertyName).Distinct());
        Assert.Empty(bed.Installs.Added);
    }

    // La firma es válida para lo que mandó Shopify, pero el dominio no es de Shopify: shop_invalid.
    [Fact]
    public async Task StartAnonymousSignedButInvalidShopThrowsShopInvalid()
    {
        var bed = new Bed();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.Anonymous().HandleAsync(Anonymous(bed, shop: "evil.com"), Ct));

        Assert.Equal(IntegrationsErrorCodes.ShopifyShopInvalid, error.Code);
    }
}

internal sealed class InMemoryShopifyInstallRepository : IShopifyInstallRepository
{
    public List<ShopifyInstall> Added { get; } = [];

    public void Add(ShopifyInstall install) => Added.Add(install);
}
