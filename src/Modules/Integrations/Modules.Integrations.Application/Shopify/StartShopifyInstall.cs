using System.Buffers.Text;
using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>Inicio A (spec 2026-10-15 §4.3, A1): desde QEP, con sesión y tenant.</summary>
public sealed record StartShopifyInstallForTenantCommand(Guid TenantId, string? Shop, string? RedirectUri)
    : ICommand<ShopifyAuthorizeResponse>;

/// <summary>Inicio B (spec 2026-10-15 §4.3, B2): el SPA reenvía lo que Shopify puso en la App URL, sin sesión.
/// <see cref="Hmac"/> firma <c>shop</c>, <c>timestamp</c> y <c>host</c> con el client secret de la app.</summary>
public sealed record StartShopifyInstallCommand(string? Shop, string? Hmac, string? Timestamp, string? Host, string? RedirectUri)
    : ICommand<ShopifyAuthorizeResponse>;

/// <summary>
/// La respuesta de los dos inicios: sólo la URL, completa. El SPA hace <c>window.location = authorizeUrl</c> y nada
/// más; el <c>state</c>, el <c>client_id</c>, los scopes y el <c>redirect_uri</c> ya van adentro, porque el
/// <c>state</c> lo guarda el backend y la pantalla no tiene por qué conocer la app ni los scopes (BFF).
/// </summary>
public sealed record ShopifyAuthorizeResponse(string AuthorizeUrl);

/// <summary>Las reglas de forma comunes: el dominio presente (la forma <c>*.myshopify.com</c> la decide
/// <see cref="ShopifyShopDomain"/>, con su propio código <c>shop_invalid</c>) y el <c>redirect_uri</c> absoluto, http(s)
/// y sin fragmento, como el de Instagram. Que el origen esté permitido lo decide el handler con la configuración.</summary>
internal static class ShopifyInstallInputRules
{
    public const int ShopMaxLength = 255;

    public const string ShopMessage = "Escribe el dominio de tu tienda, como mi-tienda.myshopify.com.";

    public static bool IsShopPresent(string? shop) =>
        !string.IsNullOrWhiteSpace(shop) && shop.Length <= ShopMaxLength && !shop.Any(char.IsControl);

    public static bool IsRedirectWellFormed(string? redirectUri) => ConnectInstagramValidator.TryParse(redirectUri, out _);
}

public sealed class StartShopifyInstallForTenantValidator : AbstractValidator<StartShopifyInstallForTenantCommand>
{
    public StartShopifyInstallForTenantValidator()
    {
        RuleFor(command => command.Shop)
            .Must(ShopifyInstallInputRules.IsShopPresent)
            .OverridePropertyName("shop")
            .WithMessage(ShopifyInstallInputRules.ShopMessage);
        RuleFor(command => command.RedirectUri)
            .Must(ShopifyInstallInputRules.IsRedirectWellFormed)
            .OverridePropertyName("redirectUri")
            .WithMessage(ConnectInstagramValidator.RedirectUriMessage);
    }
}

public sealed class StartShopifyInstallValidator : AbstractValidator<StartShopifyInstallCommand>
{
    public StartShopifyInstallValidator()
    {
        RuleFor(command => command.Shop)
            .Must(ShopifyInstallInputRules.IsShopPresent)
            .OverridePropertyName("shop")
            .WithMessage(ShopifyInstallInputRules.ShopMessage);
        RuleFor(command => command.RedirectUri)
            .Must(ShopifyInstallInputRules.IsRedirectWellFormed)
            .OverridePropertyName("redirectUri")
            .WithMessage(ConnectInstagramValidator.RedirectUriMessage);
    }
}

/// <summary>Los errores de la instalación de Shopify (spec 2026-10-15 §7.1), cada uno con la excepción que
/// <c>ApiExceptionHandler</c> responde con su status: el handler no arma ninguno a mano.</summary>
internal static class ShopifyInstallErrors
{
    public static RequestUnauthorizedException HmacInvalid() =>
        new(IntegrationsErrorCodes.ShopifyHmacInvalid, "The Shopify signature of the request does not match.");

    public static IntegrationsDomainException ShopInvalid() =>
        new(
            IntegrationsErrorCodes.ShopifyShopInvalid,
            "The shop domain does not have the *.myshopify.com shape.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["shop"] = ["El dominio tiene que ser el de Shopify, como mi-tienda.myshopify.com."],
            });

    public static ResourceNotFoundException InstallNotFound() =>
        new(IntegrationsErrorCodes.ShopifyInstallNotFound, "The Shopify install was not found.");

    public static IntegrationsDomainException InstallExpired() =>
        new(IntegrationsErrorCodes.ShopifyInstallExpired, "The Shopify install expired.");

    public static RequestForbiddenException InstallTenantMismatch() =>
        new(IntegrationsErrorCodes.ShopifyInstallTenantMismatch, "The Shopify install was started from another tenant.");

    /// <summary>422 <c>validation.failed</c> en <c>errors["redirectUri"]</c>: un origen ajeno es un error del campo.</summary>
    public static ValidationException RedirectNotAllowed() =>
        new([new ValidationFailure("redirectUri", ConnectInstagramValidator.RedirectUriNotAllowedMessage)]);
}

/// <summary>Lo que comparten los dos inicios: el origen permitido, la fila con su <c>state</c> y la URL.</summary>
internal static class ShopifyInstallStarter
{
    private const int StateBytes = 32;

    public static async Task<ShopifyAuthorizeResponse> StartAsync(
        IShopifyInstallRepository installs,
        IIntegrationsUnitOfWork unitOfWork,
        IShopifyAppSettings shopifyApp,
        IClock clock,
        string shopDomain,
        string redirectUri,
        Guid? initiatedTenantId,
        Guid? initiatedBy,
        CancellationToken cancellationToken)
    {
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));
        // §12: el installId es un GUID aleatorio (v4), no uno con marca de tiempo: viaja en la URL de /shopify/link.
        var install = ShopifyInstall.Start(Guid.NewGuid(), shopDomain, state, redirectUri, initiatedTenantId, initiatedBy, clock.UtcNow);
        installs.Add(install);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ShopifyAuthorizeResponse(AuthorizeUrl(shopifyApp, shopDomain, redirectUri, state));
    }

    /// <summary>
    /// §4.3 paso 3: <c>https://{shop}/admin/oauth/authorize?client_id&amp;scope&amp;redirect_uri&amp;state</c>. Sin
    /// <c>grant_options[]=per-user</c>: el token tiene que ser offline, porque lo usan los workers sin persona detrás.
    /// </summary>
    public static string AuthorizeUrl(IShopifyAppSettings shopifyApp, string shopDomain, string redirectUri, string state) =>
        $"https://{shopDomain}/admin/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(shopifyApp.ClientId!)}"
        + $"&scope={Uri.EscapeDataString(string.Join(',', shopifyApp.Scopes))}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={state}";

    public static void EnsureRedirectAllowed(IShopifyAppSettings shopifyApp, string? redirectUri)
    {
        // El validador ya comprobó la forma, así que el TryParse acá no puede fallar.
        if (!ConnectInstagramValidator.TryParse(redirectUri, out var uri) || !shopifyApp.IsRedirectAllowed(uri))
        {
            throw ShopifyInstallErrors.RedirectNotAllowed();
        }
    }

    public static string NormalizeShop(string? shop) =>
        ShopifyShopDomain.TryNormalize(shop, out var normalized) ? normalized : throw ShopifyInstallErrors.ShopInvalid();
}

/// <summary>
/// Inicio A, en este orden: autoriza (tenant y <c>integrations.connection.manage</c>, doble capa) → módulo
/// <c>shopify</c> activo → app configurada (D-M3: sin ella el proveedor está oculto, mismo 403) → validador →
/// dominio → origen del retorno → membresía → fila con <c>initiated_tenant_id</c> e <c>initiated_by</c>.
/// </summary>
public sealed class StartShopifyInstallForTenantHandler(
    IShopifyInstallRepository installs,
    IIntegrationsUnitOfWork unitOfWork,
    IShopifyAppSettings shopifyApp,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<StartShopifyInstallForTenantCommand> validator)
    : ICommandHandler<StartShopifyInstallForTenantCommand, ShopifyAuthorizeResponse>
{
    public async Task<ShopifyAuthorizeResponse> HandleAsync(StartShopifyInstallForTenantCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Shopify, cancellationToken);
        if (!shopifyApp.IsConfigured)
        {
            throw ProviderVisibility.Hidden(IntegrationProviders.Shopify.Key);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var shopDomain = ShopifyInstallStarter.NormalizeShop(command.Shop);
        ShopifyInstallStarter.EnsureRedirectAllowed(shopifyApp, command.RedirectUri);
        var member = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        return await ShopifyInstallStarter.StartAsync(
            installs, unitOfWork, shopifyApp, clock, shopDomain, command.RedirectUri!, command.TenantId, member, cancellationToken);
    }
}

/// <summary>
/// Inicio B (anónimo, limitador <c>public</c>), en este orden: validador → HMAC de la query con el client secret (sin
/// la app configurada no hay con qué verificar: también es <c>hmac_invalid</c>) → dominio → origen del retorno → fila
/// sin tenant. La firma se verifica sobre lo que mandó Shopify, antes de normalizar nada.
/// </summary>
public sealed class StartShopifyInstallHandler(
    IShopifyInstallRepository installs,
    IIntegrationsUnitOfWork unitOfWork,
    IShopifyAppSettings shopifyApp,
    IClock clock,
    IValidator<StartShopifyInstallCommand> validator)
    : ICommandHandler<StartShopifyInstallCommand, ShopifyAuthorizeResponse>
{
    public async Task<ShopifyAuthorizeResponse> HandleAsync(StartShopifyInstallCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!shopifyApp.IsConfigured
            || string.IsNullOrEmpty(command.Hmac)
            || string.IsNullOrEmpty(command.Timestamp)
            || !ShopifyHmac.VerifyQuery(SignedQuery(command), shopifyApp.ClientSecret!))
        {
            throw ShopifyInstallErrors.HmacInvalid();
        }

        var shopDomain = ShopifyInstallStarter.NormalizeShop(command.Shop);
        ShopifyInstallStarter.EnsureRedirectAllowed(shopifyApp, command.RedirectUri);

        return await ShopifyInstallStarter.StartAsync(
            installs, unitOfWork, shopifyApp, clock, shopDomain, command.RedirectUri!, null, null, cancellationToken);
    }

    /// <summary>Lo que Shopify puso en la App URL (V-7): <c>shop</c>, <c>timestamp</c>, <c>host</c> si vino, y la firma.</summary>
    private static Dictionary<string, string> SignedQuery(StartShopifyInstallCommand command)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shop"] = command.Shop!,
            ["timestamp"] = command.Timestamp!,
            [ShopifyHmac.QueryHmacKey] = command.Hmac!,
        };
        if (command.Host is not null)
        {
            query["host"] = command.Host;
        }

        return query;
    }
}
