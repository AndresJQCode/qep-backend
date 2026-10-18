using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// El canje (spec 2026-10-15 §4.3 paso 4): el SPA reenvía lo que Shopify puso en el callback, más el <c>redirect_uri</c>
/// con el que pidió la autorización. <see cref="Hmac"/> firma <c>code</c>, <c>shop</c>, <c>state</c>, <c>timestamp</c> y
/// <see cref="Host"/> si vino. <see cref="Host"/> no está en el cuerpo del §7: se agregó porque Shopify lo pone en el
/// callback (V-1) y, si el SPA no lo reenvía, la firma no cierra.
/// </summary>
public sealed record CompleteShopifyInstallCommand(
    string? Code,
    string? Shop,
    string? State,
    string? Hmac,
    string? Timestamp,
    string? RedirectUri,
    string? Host = null)
    : ICommand<ShopifyInstallCompletedResponse>
{
    // El code canjea un token y el state es la llave de la fila: ninguno de los dos va a un log.
    public override string ToString() => $"CompleteShopifyInstallCommand {{ Shop = {Shop}, RedirectUri = {RedirectUri} }}";
}

/// <summary>
/// Lo que el SPA necesita para decidir el paso 5 sin otra llamada: con <see cref="InitiatedTenantId"/> y sesión en ese
/// tenant enlaza solo; sin él, va a <c>/shopify/link/{installId}</c> (o a login/registro con <c>returnTo</c>).
/// <see cref="LinkedTenant"/> viaja aparte, y no como error, porque la pantalla tiene que mostrar a cuál tenant ya está
/// enlazada la tienda. <see cref="ExpiresAt"/> es la fecha que la pantalla promete («la instalación queda guardada hasta…»).
/// </summary>
public sealed record ShopifyInstallCompletedResponse(
    Guid InstallId,
    string ShopDomain,
    string ShopName,
    DateTimeOffset ExpiresAt,
    Guid? InitiatedTenantId,
    LinkedTenantResponse? LinkedTenant);

/// <summary>El tenant al que la tienda ya está enlazada (la ruta <c>("shopify", shopDomain)</c>), con su nombre para mostrar.</summary>
public sealed record LinkedTenantResponse(Guid TenantId, string Name);

public sealed class CompleteShopifyInstallValidator : AbstractValidator<CompleteShopifyInstallCommand>
{
    /// <summary>Holgado: Shopify no documenta el largo del <c>code</c>; sólo evita cuerpos absurdos.</summary>
    public const int CodeMaxLength = 512;

    public CompleteShopifyInstallValidator()
    {
        RuleFor(command => command.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Length <= CodeMaxLength)
            .OverridePropertyName("code")
            .WithMessage("Falta el código de autorización de Shopify. Vuelve a instalar la app desde tu tienda.");
        RuleFor(command => command.State)
            .Must(state => !string.IsNullOrWhiteSpace(state) && state.Length <= ShopifyInstall.StateMaxLength)
            .OverridePropertyName("state")
            .WithMessage("Falta el identificador de la instalación. Vuelve a instalar la app desde tu tienda.");
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

/// <summary>
/// El canje (anónimo, limitador <c>public</c>), en este orden: validador → HMAC del callback con el client secret (sin
/// la app configurada, <c>hmac_invalid</c>, como el inicio B) → dominio → fila por <c>state</c>, de la misma tienda y con
/// el mismo <c>redirect_uri</c> (si no, <c>install_not_found</c>: no se revela que el <c>state</c> existe para otra) → ya
/// canjeada = <c>install_not_found</c>, vencida = <c>install_expired</c> → llave de cifrado disponible (503 antes de
/// gastar el <c>code</c>) → canje → scopes → lectura de la tienda → token cifrado con AAD <c>installId</c> +
/// <c>accessToken</c> → <c>UPDATE</c> condicionado que consume el <c>state</c> → ruta existente.
/// </summary>
public sealed class CompleteShopifyInstallHandler(
    IShopifyInstallRepository installs,
    IShopifyAdminGateway gateway,
    IShopifyAppSettings shopifyApp,
    ISecretProtector protector,
    IConnectionRoutes routes,
    ITenantDirectory tenantDirectory,
    IClock clock,
    IValidator<CompleteShopifyInstallCommand> validator)
    : ICommandHandler<CompleteShopifyInstallCommand, ShopifyInstallCompletedResponse>
{
    public async Task<ShopifyInstallCompletedResponse> HandleAsync(
        CompleteShopifyInstallCommand command, CancellationToken cancellationToken)
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
        var install = await installs.FindByStateAsync(command.State!, cancellationToken);
        if (install is null
            || !string.Equals(install.ShopDomain, shopDomain, StringComparison.Ordinal)
            || !string.Equals(install.RedirectUri, command.RedirectUri, StringComparison.Ordinal)
            || install.IsExchanged)
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        if (!install.CanBeExchanged(clock.UtcNow))
        {
            throw ShopifyInstallErrors.InstallExpired();
        }

        SecretProtectionGuard.EnsureAvailable(protector);

        var grant = await CallAsync(() => gateway.ExchangeCodeAsync(shopDomain, command.Code!, cancellationToken));
        EnsureScopes(grant.Scopes);
        var shop = await CallAsync(() => gateway.GetShopAsync(shopDomain, grant.AccessToken, cancellationToken));

        var token = protector.Protect(install.Id, ShopifyFieldKeys.AccessToken, grant.AccessToken);
        install.MarkExchanged(shop.Id, shop.Name, grant.Scopes, token, clock.UtcNow);
        if (!await installs.TryCompleteExchangeAsync(install, cancellationToken))
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        return new ShopifyInstallCompletedResponse(
            install.Id,
            install.ShopDomain,
            install.ShopName!,
            install.ExpiresAt,
            install.InitiatedTenantId,
            await LinkedTenantAsync(shopDomain, cancellationToken));
    }

    /// <summary>Lo que Shopify firmó en el callback: <c>code</c>, <c>shop</c>, <c>state</c>, <c>timestamp</c> y <c>host</c>
    /// si vino. Si Shopify agrega otro parámetro, la firma no cierra (V-1).</summary>
    private static Dictionary<string, string> SignedQuery(CompleteShopifyInstallCommand command)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = command.Code!,
            ["shop"] = command.Shop!,
            ["state"] = command.State!,
            ["timestamp"] = command.Timestamp!,
            [ShopifyHmac.QueryHmacKey] = command.Hmac!,
        };
        if (command.Host is not null)
        {
            query["host"] = command.Host;
        }

        return query;
    }

    // Toda falla de Shopify en el canje es la misma para la persona: el code ya se gastó (o no sirvió) y hay que volver a
    // empezar. La clase (rechazo, red, respuesta ilegible) queda en el log del gateway, no en el código.
    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ShopifyGatewayException exception)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.ShopifyCodeExchangeFailed, $"Shopify did not complete the install ({exception.Kind}).");
        }
    }

    /// <summary>Lo concedido tiene que contener todo lo configurado. Shopify no repite <c>read_x</c> cuando concede
    /// <c>write_x</c>, así que un <c>write_</c> cuenta como su <c>read_</c>.</summary>
    private void EnsureScopes(string granted)
    {
        var scopes = granted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var missing = shopifyApp.Scopes
            .Where(scope => !scopes.Contains(scope)
                && !(scope.StartsWith("read_", StringComparison.Ordinal) && scopes.Contains("write_" + scope["read_".Length..])))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.ShopifyScopesMissing,
                $"Shopify did not grant every configured scope; missing: {string.Join(',', missing)}.");
        }
    }

    // La ruta es cross-tenant a propósito: la tienda puede estar enlazada a un tenant que no es de quien instala, y la
    // pantalla tiene que poder decirlo (Review Focus 3). Una conexión borrada no deja ruta.
    private async Task<LinkedTenantResponse?> LinkedTenantAsync(string shopDomain, CancellationToken cancellationToken)
    {
        var route = await routes.FindAsync(IntegrationProviders.Shopify.Key, shopDomain, cancellationToken);
        if (route is null)
        {
            return null;
        }

        var name = await tenantDirectory.GetDisplayNameAsync(new TenantId(route.TenantId), cancellationToken);
        return new LinkedTenantResponse(route.TenantId, name ?? string.Empty);
    }
}
