using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// De dónde salen los módulos de un tenant. Hoy la tabla local de Tenancy; mañana, el control plane,
/// sin tocar el enforcement (spec 2026-10-07, «Puertos»).
/// </summary>
public interface ITenantModules
{
    /// <summary><c>null</c> = el tenant no existe en <c>tenancy.tenants</c> (sólo pasa con el stub de
    /// desarrollo).</summary>
    Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Se commitea con <see cref="ITenancyUnitOfWork"/>.</summary>
public interface ITenantModuleRepository
{
    void Add(TenantModule tenantModule);
}

/// <summary>Los módulos con los que nace un tenant del signup.</summary>
public interface ITenantModuleDefaults
{
    IReadOnlyCollection<TenantModuleKey> ForNewTenants { get; }
}
