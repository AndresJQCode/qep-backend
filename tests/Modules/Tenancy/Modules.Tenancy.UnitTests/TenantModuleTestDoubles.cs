using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>El puerto de módulos con una respuesta fija. <c>null</c> es el tenant que no existe en
/// <c>tenancy.tenants</c> (sólo pasa con el stub de desarrollo). Cada prueba arma el suyo: un doble
/// estático compartido acumularía <see cref="Asked"/> entre pruebas.</summary>
internal sealed class FixedTenantModules(TenantModuleSet? set) : ITenantModules
{
    /// <summary>Un doble con todos los módulos contratados, nuevo en cada llamada.</summary>
    public static FixedTenantModules AllEnabled() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    public List<Guid> Asked { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(set);
    }
}
