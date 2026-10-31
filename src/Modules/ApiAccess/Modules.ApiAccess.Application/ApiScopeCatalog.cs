using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ApiAccess.Application;

/// <summary>
/// Los scopes que los módulos ofrecen, filtrados por lo que el tenant tiene prendido. Cada módulo
/// registra los suyos como <see cref="ApiScopeDefinition"/> en el composition root, así ApiAccess no
/// los conoce ni se acopla a ninguno.
/// </summary>
public sealed class ApiScopeCatalog
{
    private readonly ITenantModules _tenantModules;

    public ApiScopeCatalog(IEnumerable<ApiScopeDefinition> definitions, ITenantModules tenantModules)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _tenantModules = tenantModules;
        All = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, ApiScopeDefinition> All { get; }

    /// <summary>
    /// Los scopes cuyo módulo está prendido para el tenant. Sin fila de módulos (el stub de desarrollo)
    /// no ofrece ninguno: una clave que no se puede usar es peor que no poder crearla.
    /// </summary>
    public async Task<IReadOnlyList<ApiScopeDefinition>> AvailableForAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var modules = await _tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null)
        {
            return [];
        }

        return All.Values
            .Where(definition => modules.IsEnabled(TenantModuleKey.Parse(definition.RequiredModule)))
            .ToArray();
    }
}
