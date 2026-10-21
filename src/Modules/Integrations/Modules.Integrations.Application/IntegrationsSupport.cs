using BuildingBlocks.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Copia de <c>PosAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403.
/// Nunca 404: confirmaría que el id existe en otro tenant (doble capa, <c>CLAUDE.md</c>).
/// </summary>
internal static class IntegrationsAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this integrations operation for this tenant.");
}

/// <summary>404 dentro del tenant de la ruta: el repositorio filtra por tenant.</summary>
internal static class IntegrationsNotFound
{
    public static ResourceNotFoundException Connection(Guid connectionId) =>
        new(IntegrationsErrorCodes.NotFound, $"Connection '{connectionId}' was not found.");
}

/// <summary>
/// Spec 2026-10-08, «Visibilidad para un tenant»: lo de un proveedor oculto responde 403
/// <c>tenancy.module_not_enabled</c>, igual que el resto de la app con un módulo apagado.
/// </summary>
internal static class ProviderVisibility
{
    public static async Task<IReadOnlyList<IntegrationProvider>> VisibleAsync(
        IIntegrationProviderCatalog catalog,
        ITenantModules tenantModules,
        IMetaAppSettings metaApp,
        IInstagramAppSettings instagramApp,
        IShopifyAppSettings shopifyApp,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        return catalog.All
            .Where(provider => provider.IsVisibleFor(modules))
            // D-M3: sin la app de Meta configurada (sólo fuera de Production) no hay cómo conectar. Mismo criterio
            // con el caso de uso Instagram (spec 2026-10-14 §6.1) y con la app de Shopify (spec 2026-10-15 §4.2).
            .Where(provider => provider.Onboarding switch
            {
                ProviderOnboarding.MetaEmbeddedSignup => metaApp.IsConfigured,
                ProviderOnboarding.InstagramLogin => instagramApp.IsConfigured,
                ProviderOnboarding.ShopifyOAuth => shopifyApp.IsConfigured,
                _ => true,
            })
            .ToArray();
    }

    public static async Task EnsureVisibleAsync(
        ITenantModules tenantModules, Guid tenantId, IntegrationProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            throw Hidden(provider.Key);
        }
    }

    public static RequestForbiddenException Hidden(string providerKey) =>
        new(
            TenantModuleGuard.ModuleNotEnabledCode,
            $"The '{providerKey}' integration is not available: none of the modules that use it is enabled for this tenant.");

    /// <summary>Plan 2026-10-21: un proveedor que exige habilitación sólo se usa si el operador lo habilitó para el
    /// tenant. Sin esa exigencia no consulta nada, para que los consumidores de los demás proveedores (la bandeja de
    /// Messaging resuelve conexiones en cada envío) no paguen una lectura más.</summary>
    public static async Task<bool> IsEnabledAsync(
        ITenantProviders tenantProviders, Guid tenantId, IntegrationProvider provider, CancellationToken cancellationToken) =>
        !provider.RequiresTenantEnablement
        || provider.IsEnabledFor(await tenantProviders.FindEnabledAsync(tenantId, cancellationToken));

    /// <summary>Lo exigen crear, editar, probar, pausar y reanudar; leer, listar y borrar no, para que el tenant vea y
    /// pueda limpiar lo que ya tenía. Va <b>después</b> de <see cref="EnsureVisibleAsync"/>: con el módulo apagado la
    /// respuesta sigue siendo <c>tenancy.module_not_enabled</c>.</summary>
    public static async Task EnsureEnabledAsync(
        ITenantProviders tenantProviders, Guid tenantId, IntegrationProvider provider, CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(tenantProviders, tenantId, provider, cancellationToken))
        {
            throw NotEnabled(provider.Key);
        }
    }

    public static RequestForbiddenException NotEnabled(string providerKey) =>
        new(
            IntegrationsErrorCodes.ProviderNotEnabled,
            $"The '{providerKey}' integration is not enabled for this tenant.");
}

internal static class ConnectionLoader
{
    /// <summary>404 si no está en el tenant; 403 si su proveedor no es visible o ya no está en el
    /// catálogo (P22).</summary>
    public static async Task<(IntegrationConnection Connection, IntegrationProvider Provider)> LoadVisibleAsync(
        IIntegrationConnectionRepository repository,
        IIntegrationProviderCatalog catalog,
        ITenantModules tenantModules,
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken)
            ?? throw IntegrationsNotFound.Connection(connectionId);
        var provider = catalog.Find(connection.ProviderKey) ?? throw ProviderVisibility.Hidden(connection.ProviderKey);
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, tenantId, provider, cancellationToken);
        return (connection, provider);
    }

    /// <summary>Lo de <see cref="LoadVisibleAsync"/> y, además, el proveedor habilitado para el tenant (403
    /// <c>integrations.provider.not_enabled</c>): el camino de editar, probar, pausar y reanudar.</summary>
    public static async Task<(IntegrationConnection Connection, IntegrationProvider Provider)> LoadEnabledAsync(
        IIntegrationConnectionRepository repository,
        IIntegrationProviderCatalog catalog,
        ITenantModules tenantModules,
        ITenantProviders tenantProviders,
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadVisibleAsync(repository, catalog, tenantModules, tenantId, connectionId, cancellationToken);
        await ProviderVisibility.EnsureEnabledAsync(tenantProviders, tenantId, loaded.Provider, cancellationToken);
        return loaded;
    }
}

internal static class ConnectionMapping
{
    public static ProviderResponse ToProvider(
        IntegrationProvider provider,
        int connectionCount,
        bool platformEnabled,
        IMetaAppSettings metaApp,
        IInstagramAppSettings instagramApp) =>
        new(
            provider.Key,
            provider.DisplayName,
            provider.Category.ToString(),
            provider.CatalogFields
                .Select(field => new ProviderFieldResponse(field.Key, field.Label, field.Kind.ToString(), field.Required, field.MaxLength))
                .ToArray(),
            provider.MaxConnections,
            connectionCount,
            provider.Onboarding switch
            {
                ProviderOnboarding.MetaEmbeddedSignup =>
                    new ProviderOnboardingResponse(nameof(ProviderOnboarding.MetaEmbeddedSignup), metaApp.AppId, metaApp.ConfigId, metaApp.GraphApiVersion),
                ProviderOnboarding.InstagramLogin =>
                    new ProviderOnboardingResponse(
                        nameof(ProviderOnboarding.InstagramLogin), instagramApp.AppId, null, instagramApp.GraphApiVersion,
                        InstagramLogin.AuthorizeUrl, InstagramLogin.RequiredScopes),
                // Spec 2026-10-15 §4.3: sin authorizeUrl. La arma el inicio de la instalación, porque lleva el dominio de
                // la tienda, el state y el redirect_uri de cada intento; aquí no hay nada fijo que mandar.
                ProviderOnboarding.ShopifyOAuth => new ProviderOnboardingResponse(nameof(ProviderOnboarding.ShopifyOAuth), null, null, null),
                _ => new ProviderOnboardingResponse(nameof(ProviderOnboarding.Form), null, null, null),
            },
            platformEnabled);

    public static async Task<ConnectionResponse> ToResponseAsync(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IConnectionAuthorNames authorNames,
        bool providerEnabled,
        CancellationToken cancellationToken)
    {
        var names = await authorNames.FindAsync(connection.TenantId, [connection.CreatedBy], cancellationToken);
        return ToResponse(connection, provider, protector, names, providerEnabled);
    }

    public static ConnectionResponse ToResponse(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IReadOnlyDictionary<Guid, string> authorNames,
        bool providerEnabled) =>
        new(
            connection.Id,
            connection.ProviderKey,
            connection.Name,
            connection.Status.ToString(),
            provider.PublicFields.ToDictionary(
                field => field.Key,
                field => connection.Fields.GetValueOrDefault(field.Key),
                StringComparer.Ordinal),
            provider.SecretFields.ToDictionary(
                field => field.Key,
                field => SecretState(connection, field.Key, protector),
                StringComparer.Ordinal),
            connection.LastVerifiedAt,
            connection.LastFailureAt,
            connection.LastFailureCode,
            connection.CreatedAt,
            new ConnectionAuthorResponse(connection.CreatedBy, authorNames.GetValueOrDefault(connection.CreatedBy)),
            connection.UpdatedAt,
            connection.Version,
            providerEnabled);

    private static SecretStateResponse SecretState(IntegrationConnection connection, string fieldKey, ISecretProtector protector)
    {
        var stored = connection.Secrets.FirstOrDefault(secret => string.Equals(secret.FieldKey, fieldKey, StringComparison.Ordinal));
        if (stored is null)
        {
            return new SecretStateResponse(Configured: false, UpdatedAt: null, Readable: false);
        }

        // Spec, «readable»: descifra de verdad y descarta el valor.
        var readable = protector.TryUnprotect(connection.Id, fieldKey, stored.Protected, out _);
        return new SecretStateResponse(Configured: true, stored.UpdatedAt, readable);
    }
}
