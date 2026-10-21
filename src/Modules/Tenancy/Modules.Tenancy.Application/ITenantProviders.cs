using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Qué proveedores tiene habilitados un tenant (plan 2026-10-21). Mismo molde que
/// <see cref="ITenantModules"/>: hoy la tabla local de Tenancy, mañana el control plane.
/// </summary>
public interface ITenantProviders
{
    /// <summary>Las claves con fila <c>active</c>; vacío si no tiene ninguna (D2: sin fila = deshabilitado).
    /// <c>null</c> = el tenant no existe en <c>tenancy.tenants</c> (sólo pasa con el stub de desarrollo),
    /// mismo contrato que <see cref="ITenantModules.FindAsync"/>.</summary>
    Task<IReadOnlySet<string>?> FindEnabledAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Se commitea con <see cref="ITenancyUnitOfWork"/>.</summary>
public interface ITenantProviderRepository
{
    void Add(TenantProvider provider);

    /// <summary>Todas las filas del tenant, inactivas incluidas y con tracking: es el estado guardado que el
    /// cambio de la consola valida y modifica, como <see cref="ITenantModuleRepository.ListByTenantAsync"/>.</summary>
    Task<IReadOnlyList<TenantProvider>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken);
}
