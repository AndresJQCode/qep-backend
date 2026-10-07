using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Los módulos de un tenant nuevo del signup. Pública, a diferencia del resto de la infraestructura
/// de Tenancy, sólo para probarla sin <c>InternalsVisibleTo</c>. No hay lista configurable (YAGNI):
/// el paquete por tenant es asunto del control plane.
/// </summary>
public sealed class TenantModuleDefaults(IOptions<EntitlementsOptions> options) : ITenantModuleDefaults
{
    public IReadOnlyCollection<TenantModuleKey> ForNewTenants =>
        options.Value.GrantDefaultModulesOnSignup ? TenantModuleKeys.DefaultForNewTenants : [];
}
