using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>El enlace (spec 2026-10-15 §4.3 paso 6 y §7): <c>{ installId, name? }</c> en el tenant de la ruta. Sin
/// <see cref="Name"/>, la conexión se llama como la tienda. Nada de esto es secreto: el token vive en la fila de la
/// instalación y nunca pasa por el cuerpo.</summary>
public sealed record LinkShopifyStoreCommand(Guid TenantId, Guid InstallId, string? Name) : ICommand<ConnectionResponse>;

/// <summary>Cada regla nombra su clave en camelCase, la que lee la pantalla en <c>errors</c>.</summary>
public sealed class LinkShopifyStoreValidator : AbstractValidator<LinkShopifyStoreCommand>
{
    public LinkShopifyStoreValidator()
    {
        RuleFor(command => command.InstallId)
            .NotEqual(Guid.Empty)
            .OverridePropertyName("installId")
            .WithMessage("Falta la instalación de Shopify. Vuelve a instalar la app desde tu tienda.");
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)))
            .When(command => command.Name is not null);
    }
}

/// <summary>
/// El enlace de una tienda al tenant elegido, en este orden (<see cref="IShopifyInstallRepository"/> no recibe
/// tenant, así que toda la autorización vive aquí; <c>GET /installs/{id}</c> lo lee cualquier autenticado con el id):
/// <list type="number">
/// <item>tenant y <c>integrations.connection.manage</c> (doble capa) → módulo <c>shopify</c> activo → app configurada;</item>
/// <item>validador → llave de cifrado → membresía;</item>
/// <item>la instalación: desconocida o sin canjear = <c>install_not_found</c>; iniciada desde otro tenant (A) =
/// <c>install_tenant_mismatch</c> (403); vencida, sin refresh token o con un token que ya no descifra =
/// <c>install_expired</c>; con el token de acceso por vencer, se refresca antes de seguir (spec 2026-11-12 §6.1);</item>
/// <item>la ruta <c>("shopify", shopDomain)</c>: de otro tenant = <c>shop_already_linked</c> con su nombre; del mismo,
/// reenlace de esa conexión (sin tope, token nuevo, <c>Active</c> aunque estuviera pausada: §4.1 «la
/// reactiva»);</item>
/// <item>conexión nueva: tope de 20 y nombre no repetido;</item>
/// <item>webhooks que falten (<see cref="ShopifyWebhookRegistration"/>); si Shopify falla, la conexión se guarda igual en
/// <c>NeedsAttention</c> con <c>webhooks_not_registered</c> y el probador lo reintenta;</item>
/// <item>conexión, ruta, borrado de la instalación y auditoría <c>onboarding:shopify_oauth</c> con la variante, en un
/// solo guardado. La carrera de dos tenants por la misma tienda la cierra <c>IX_connection_routes_provider_external</c>.</item>
/// </list>
/// </summary>
public sealed class LinkShopifyStoreHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IConnectionRouteRepository routeRepository,
    IConnectionRoutes routes,
    IShopifyInstallRepository installs,
    IShopifyAdminGateway gateway,
    IShopifyAppSettings shopifyApp,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<LinkShopifyStoreCommand> validator)
    : ICommandHandler<LinkShopifyStoreCommand, ConnectionResponse>
{
    /// <summary>Spec §4.3 paso 6: la auditoría dice por qué flujo entró la conexión, como <c>onboarding:instagram_login</c>.
    /// La variante va como una clave más: <c>onboarding:shopify_oauth:A</c> (desde QEP) o <c>:B</c> (desde Shopify).</summary>
    public const string OnboardingAuditField = "onboarding:shopify_oauth";

    public async Task<ConnectionResponse> HandleAsync(LinkShopifyStoreCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Shopify, cancellationToken);
        var provider = catalog.Find(IntegrationProviders.Shopify.Key)
            ?? throw new InvalidOperationException("The shopify provider is not in the catalog.");
        if (!shopifyApp.IsConfigured)
        {
            throw ProviderVisibility.Hidden(provider.Key);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);
        SecretProtectionGuard.EnsureAvailable(protector);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var install = await installs.FindAsync(command.InstallId, cancellationToken);
        if (install is null || !install.IsExchanged)
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        if (install.InitiatedTenantId is { } initiated && initiated != command.TenantId)
        {
            throw ShopifyInstallErrors.InstallTenantMismatch();
        }

        var now = clock.UtcNow;
        if (now >= install.ExpiresAt)
        {
            throw ShopifyInstallErrors.InstallExpired();
        }

        // El dominio es el external_id de la ruta (varchar(64)): uno más largo sería un 500 de la base, no un 422. El inicio
        // ya lo acota; esto lo repite donde se escribe.
        var shopDomain = install.ShopDomain;
        if (shopDomain.Length > IntegrationConnectionRoute.ExternalIdMaxLength
            || !ShopifyShopDomain.TryNormalize(shopDomain, out var normalized)
            || !string.Equals(normalized, shopDomain, StringComparison.Ordinal))
        {
            throw ShopifyInstallErrors.ShopInvalid();
        }

        // Llave retirada o bytes dañados: no hay con qué enlazar y la salida para la persona es volver a instalar. Una fila
        // canjeada antes de los tokens que expiran (spec 2026-11-12) no tiene refresh token: tampoco sirve.
        var (accessToken, refreshToken) = ReadTokens(install);
        (install, accessToken, refreshToken) = await EnsureFreshInstallTokenAsync(install, accessToken, refreshToken, now, cancellationToken);

        // D3: el reenlace abre la transacción del candado de la conexión; se suelta al guardar o al salir.
        await using var relink = new RelinkTransaction();
        var existing = await FindRelinkAsync(command.TenantId, shopDomain, relink, cancellationToken);
        var name = NameFor(command.Name, existing, install);
        await EnsureNameFreeAsync(command.TenantId, provider, name, existing, cancellationToken);
        if (existing is null && await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached, $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        // Local sin túnel no hay URL pública: no se registra nada y la conciliación cubre (D-S17, §4.2).
        ShopifyWebhookRegistrationResult? registration = shopifyApp.WebhookCallbackUrl is { } callback
            ? await ShopifyWebhookRegistration.EnsureAsync(gateway, shopDomain, accessToken, callback, cancellationToken)
            : null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShopifyFieldKeys.ShopDomain] = shopDomain,
            [ShopifyFieldKeys.ShopId] = install.ShopId!,
            [ShopifyFieldKeys.Scopes] = install.Scopes!,
            [ShopifyFieldKeys.TokenExpiresAt] = ShopifyTokenRefresh.Format(install.AccessTokenExpiresAt!.Value),
            [ShopifyFieldKeys.RefreshTokenExpiresAt] = ShopifyTokenRefresh.Format(install.RefreshTokenExpiresAt!.Value),
        };
        if (!string.IsNullOrWhiteSpace(install.ShopName))
        {
            fields[ShopifyFieldKeys.ShopName] = install.ShopName.Trim();
        }

        if (registration is { SubscriptionIds.Count: > 0 })
        {
            fields[ShopifyFieldKeys.WebhookSubscriptionIds] = registration.JoinedIds;
        }
        else if (registration is null && existing?.Fields.TryGetValue(ShopifyFieldKeys.WebhookSubscriptionIds, out var kept) == true)
        {
            fields[ShopifyFieldKeys.WebhookSubscriptionIds] = kept;
        }

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShopifyFieldKeys.AccessToken] = accessToken,
            [ShopifyFieldKeys.RefreshToken] = refreshToken,
        };
        string[] onboarding = [OnboardingAuditField, $"{OnboardingAuditField}:{(install.InitiatedTenantId is null ? "B" : "A")}"];

        IntegrationConnection connection;
        var wasActive = false;
        if (existing is not null)
        {
            // §4.1: reenlace tras desinstalar. Mismo id, token nuevo; verified saca de NeedsAttention. Una pausada también
            // vuelve a Active (§4.1: «la reactiva»), con su auditoría de resume: reinstalar es pedir que funcione.
            wasActive = existing.Status == ConnectionStatus.Active;
            var changed = existing.Update(provider, name, fields, secrets, protector.Protect, now, verified: true);
            connection = existing;
            ConnectionAudit.ByMember(
                auditRecorder, executionContext, connection, ConnectionAuditActions.Updated, [.. changed, .. onboarding], now);
            if (connection.Status == ConnectionStatus.Paused)
            {
                connection.Resume(now);
                ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Resumed, [], now);
            }
        }
        else
        {
            connection = IntegrationConnection.Create(provider, command.TenantId, name, fields, secrets, protector.Protect, author, now);
            repository.Add(connection);
            routeRepository.Add(IntegrationConnectionRoute.Create(provider.Key, shopDomain, accountId: null, command.TenantId, connection.Id));
            ConnectionAudit.ByMember(
                auditRecorder, executionContext, connection, ConnectionAuditActions.Created,
                [.. ConnectionAudit.KeysOf(connection), .. onboarding], now);
        }

        if (registration is { Succeeded: false })
        {
            connection.MarkNeedsAttention(ConnectionFailureCodes.WebhooksNotRegistered, now);
            ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.NeedsAttention, [], now);
            // Sólo una conexión que los consumidores ya usaban necesita el aviso; una nueva todavía no la tiene nadie.
            if (wasActive)
            {
                eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
            }
        }

        installs.Remove(install);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Este proveedor no exige habilitación por tenant (plan 2026-10-21), así que no se consulta: si algún día la
        // exigiera, este flujo tendría que llamar a ProviderVisibility.EnsureEnabledAsync antes de conectar.
        return await ConnectionMapping.ToResponseAsync(
            connection, provider, protector, authorNames, providerEnabled: !provider.RequiresTenantEnablement, cancellationToken);
    }

    /// <summary>La conexión del mismo tenant para esa tienda, o <c>null</c> si no hay ruta. La ruta es cross-tenant a
    /// propósito: si es de otro, la tienda está tomada y el error dice por quién (el nombre, nada más).
    /// <para>Spec 2026-11-12 §6.2 (D3): el reenlace reemplaza los secretos de una conexión que el worker puede estar
    /// refrescando, así que abre una transacción y toma el candado del token de esa conexión <b>antes</b> de cargarla: si
    /// el worker guardó mientras tanto, se carga lo suyo y no hay 412. Una conexión nueva no abre transacción: dentro de
    /// una, el choque de <c>IX_connection_routes_provider_external</c> la abortaría y no se podría consultar quién
    /// ganó.</para></summary>
    private async Task<IntegrationConnection?> FindRelinkAsync(
        Guid tenantId, string shopDomain, RelinkTransaction relink, CancellationToken cancellationToken)
    {
        var route = await routes.FindAsync(IntegrationProviders.Shopify.Key, shopDomain, cancellationToken);
        if (route is null)
        {
            return null;
        }

        if (route.TenantId != tenantId)
        {
            var tenantName = await tenantDirectory.GetDisplayNameAsync(new TenantId(route.TenantId), cancellationToken);
            throw ShopAlreadyLinked(tenantName);
        }

        relink.Inner = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockShopifyTokenAsync(route.ConnectionId, cancellationToken);

        // La ruta se borra en cascada con su conexión: una ruta del tenant sin conexión no debería existir.
        return await repository.FindAsync(tenantId, route.ConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("A shopify route of this tenant points to no connection.");
    }

    /// <summary>Los dos tokens de la instalación en claro. Sin refresh token (fila canjeada antes del spec 2026-11-12) o con
    /// uno que no descifra, no hay con qué enlazar: <c>install_expired</c>, que pide volver a instalar desde la tienda.</summary>
    private (string AccessToken, string RefreshToken) ReadTokens(ShopifyInstall install)
    {
        if (install.ProtectedRefreshToken is null
            || install.AccessTokenExpiresAt is null
            || install.RefreshTokenExpiresAt is null
            || !protector.TryUnprotect(
                install.Id, ShopifyFieldKeys.AccessToken, new ProtectedSecret(install.KeyId!, install.ProtectedToken!), out var accessToken)
            || !protector.TryUnprotect(
                install.Id, ShopifyFieldKeys.RefreshToken, new ProtectedSecret(install.KeyId!, install.ProtectedRefreshToken), out var refreshToken))
        {
            throw ShopifyInstallErrors.InstallExpired();
        }

        return (accessToken, refreshToken);
    }

    /// <summary>
    /// Spec 2026-11-12 §6.1: la instalación vive 7 días y su token, 1 hora. Si al token le queda el margen o menos, se
    /// refresca antes de usarlo, con el candado de la instalación (el mismo de §6.2, por su id) en una transacción propia:
    /// se relee la fila, y si otro enlace ya la refrescó se usa ese par. Un rechazo de Shopify es <c>install_expired</c> (el
    /// refresh token ya no sirve; no se crea una conexión muerta); una falla transitoria, 503 <c>provider_unreachable</c>.
    /// El par nuevo queda guardado en la instalación antes de seguir: si el enlace falla después, no se pierde.
    /// </summary>
    private async Task<(ShopifyInstall Install, string AccessToken, string RefreshToken)> EnsureFreshInstallTokenAsync(
        ShopifyInstall install, string accessToken, string refreshToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!install.NeedsTokenRefresh(now))
        {
            return (install, accessToken, refreshToken);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockShopifyTokenAsync(install.Id, cancellationToken);
        var current = await installs.FindAsync(install.Id, cancellationToken);
        if (current is null || !current.IsExchanged)
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        var (currentAccess, currentRefresh) = ReadTokens(current);
        if (!current.NeedsTokenRefresh(now))
        {
            return (current, currentAccess, currentRefresh);
        }

        ShopifyTokenGrant grant;
        try
        {
            grant = await gateway.RefreshTokenAsync(current.ShopDomain, currentRefresh, cancellationToken);
        }
        catch (ShopifyGatewayException exception) when (exception.Kind == ShopifyGatewayFailure.Rejected)
        {
            throw ShopifyInstallErrors.InstallExpired();
        }
        catch (ShopifyGatewayException exception)
        {
            throw new ServiceUnavailableException(
                IntegrationsErrorCodes.ProviderUnreachable, $"Shopify could not refresh the install token ({exception.Kind}); try again.");
        }

        current.ApplyRefreshedToken(
            protector.Protect(current.Id, ShopifyFieldKeys.AccessToken, grant.AccessToken),
            protector.Protect(current.Id, ShopifyFieldKeys.RefreshToken, grant.RefreshToken),
            now + grant.ExpiresIn,
            now + grant.RefreshTokenExpiresIn);
        if (!await installs.TryStoreRefreshedTokenAsync(current, cancellationToken))
        {
            throw ShopifyInstallErrors.InstallNotFound();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (current, grant.AccessToken, grant.RefreshToken);
    }

    /// <summary>La transacción del reenlace, si se abrió (D3): se dispone al salir del handler, después de guardar.</summary>
    private sealed class RelinkTransaction : IAsyncDisposable
    {
        public IAsyncDisposable? Inner { get; set; }

        public ValueTask DisposeAsync() => Inner?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    public static IntegrationsDomainException ShopAlreadyLinked(string? tenantName) =>
        new(
            IntegrationsErrorCodes.ShopifyShopAlreadyLinked,
            string.IsNullOrWhiteSpace(tenantName)
                ? "The shop is already linked to another tenant."
                : $"The shop is already linked to the tenant \"{tenantName}\".");

    /// <summary>El que mandó la persona; si no, el de la conexión que se reenlaza; si no, el de la tienda, recortado a lo
    /// que cabe (el nombre de Shopify puede tener hasta 255) o el dominio si vino vacío.</summary>
    private static string NameFor(string? requested, IntegrationConnection? existing, ShopifyInstall install)
    {
        if (requested is not null)
        {
            return requested.Trim();
        }

        if (existing is not null)
        {
            return existing.Name;
        }

        var shopName = install.ShopName?.Trim();
        if (string.IsNullOrEmpty(shopName) || shopName.Any(char.IsControl))
        {
            return install.ShopDomain;
        }

        return shopName.Length > IntegrationConnection.NameMaxLength ? shopName[..IntegrationConnection.NameMaxLength].TrimEnd() : shopName;
    }

    private async Task EnsureNameFreeAsync(
        Guid tenantId, IntegrationProvider provider, string name, IntegrationConnection? existing, CancellationToken cancellationToken)
    {
        if ((await repository.ListAsync(tenantId, cancellationToken)).Any(connection =>
                string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal)
                && connection.Id != existing?.Id
                && string.Equals(connection.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "Another connection of this provider already uses that name.");
        }
    }
}
