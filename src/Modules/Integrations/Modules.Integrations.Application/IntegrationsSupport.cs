using BuildingBlocks.Application;
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
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        return catalog.All
            .Where(provider => provider.IsVisibleFor(modules))
            // D-M3: sin la app de Meta configurada (sólo fuera de Production) no hay cómo conectar.
            .Where(provider => provider.Onboarding != ProviderOnboarding.MetaEmbeddedSignup || metaApp.IsConfigured)
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
}

internal static class ConnectionMapping
{
    public static ProviderResponse ToProvider(IntegrationProvider provider, int connectionCount, IMetaAppSettings metaApp) =>
        new(
            provider.Key,
            provider.DisplayName,
            provider.Category.ToString(),
            provider.CatalogFields
                .Select(field => new ProviderFieldResponse(field.Key, field.Label, field.Kind.ToString(), field.Required, field.MaxLength))
                .ToArray(),
            provider.MaxConnections,
            connectionCount,
            provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup
                ? new ProviderOnboardingResponse(nameof(ProviderOnboarding.MetaEmbeddedSignup), metaApp.AppId, metaApp.ConfigId, metaApp.GraphApiVersion)
                : new ProviderOnboardingResponse(nameof(ProviderOnboarding.Form), null, null, null));

    public static async Task<ConnectionResponse> ToResponseAsync(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IConnectionAuthorNames authorNames,
        CancellationToken cancellationToken)
    {
        var names = await authorNames.FindAsync(connection.TenantId, [connection.CreatedBy], cancellationToken);
        return ToResponse(connection, provider, protector, names);
    }

    public static ConnectionResponse ToResponse(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IReadOnlyDictionary<Guid, string> authorNames) =>
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
            connection.Version);

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
