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
/// El enlace de una tienda al tenant elegido, en este orden (ruling S6: <see cref="IShopifyInstallRepository"/> no recibe
/// tenant, así que toda la autorización vive aquí; <c>GET /installs/{id}</c> lo lee cualquier autenticado con el id):
/// <list type="number">
/// <item>tenant y <c>integrations.connection.manage</c> (doble capa) → módulo <c>shopify</c> activo → app configurada;</item>
/// <item>validador → llave de cifrado → membresía;</item>
/// <item>la instalación: desconocida o sin canjear = <c>install_not_found</c>; iniciada desde otro tenant (A) =
/// <c>install_tenant_mismatch</c> (403); vencida, o con un token que ya no descifra = <c>install_expired</c>;</item>
/// <item>la ruta <c>("shopify", shopDomain)</c>: de otro tenant = <c>shop_already_linked</c> con su nombre; del mismo,
/// reenlace de esa conexión (sin tope, token nuevo, <c>Active</c>);</item>
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
        // ya lo acota; esto lo repite donde se escribe (arrastre de la revisión de la tarea 5).
        var shopDomain = install.ShopDomain;
        if (shopDomain.Length > IntegrationConnectionRoute.ExternalIdMaxLength
            || !ShopifyShopDomain.TryNormalize(shopDomain, out var normalized)
            || !string.Equals(normalized, shopDomain, StringComparison.Ordinal))
        {
            throw ShopifyInstallErrors.ShopInvalid();
        }

        // Llave retirada o bytes dañados: no hay con qué enlazar y la salida para la persona es volver a instalar.
        if (!protector.TryUnprotect(
                install.Id, ShopifyFieldKeys.AccessToken, new ProtectedSecret(install.KeyId!, install.ProtectedToken!), out var accessToken))
        {
            throw ShopifyInstallErrors.InstallExpired();
        }

        var existing = await FindRelinkAsync(command.TenantId, shopDomain, cancellationToken);
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

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [ShopifyFieldKeys.AccessToken] = accessToken };
        string[] onboarding = [OnboardingAuditField, $"{OnboardingAuditField}:{(install.InitiatedTenantId is null ? "B" : "A")}"];

        IntegrationConnection connection;
        var wasActive = false;
        if (existing is not null)
        {
            // §4.1: reenlace tras desinstalar. Mismo id, token nuevo; verified saca de NeedsAttention (Paused sigue en pausa:
            // eso lo decidió una persona).
            wasActive = existing.Status == ConnectionStatus.Active;
            var changed = existing.Update(provider, name, fields, secrets, protector.Protect, now, verified: true);
            connection = existing;
            ConnectionAudit.ByMember(
                auditRecorder, executionContext, connection, ConnectionAuditActions.Updated, [.. changed, .. onboarding], now);
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

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    /// <summary>La conexión del mismo tenant para esa tienda, o <c>null</c> si no hay ruta. La ruta es cross-tenant a
    /// propósito: si es de otro, la tienda está tomada y el error dice por quién (Review Focus 3: el nombre, nada más).</summary>
    private async Task<IntegrationConnection?> FindRelinkAsync(Guid tenantId, string shopDomain, CancellationToken cancellationToken)
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

        // La ruta se borra en cascada con su conexión: una ruta del tenant sin conexión no debería existir.
        return await repository.FindAsync(tenantId, route.ConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("A shopify route of this tenant points to no connection.");
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
