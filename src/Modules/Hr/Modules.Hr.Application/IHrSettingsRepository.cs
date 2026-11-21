using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="InUse">
/// Los ids que ya no se pueden eliminar: referenciados por algún registro de cargo y lugar, y las sedes con algún centro.
/// </param>
public sealed record HrSettingsSnapshot(
    IReadOnlyList<Site> Sites,
    IReadOnlyList<Workplace> Workplaces,
    IReadOnlyList<Area> Areas,
    IReadOnlyList<Position> Positions,
    IReadOnlySet<Guid> InUse);

/// <summary>La configuración de la organización. Todo método lleva el tenant: una lectura nunca cruza de tenant.</summary>
public interface IHrSettingsRepository
{
    Task<Site?> FindSiteAsync(Guid tenantId, SiteId id, CancellationToken cancellationToken);

    Task<Workplace?> FindWorkplaceAsync(Guid tenantId, WorkplaceId id, CancellationToken cancellationToken);

    Task<Area?> FindAreaAsync(Guid tenantId, AreaId id, CancellationToken cancellationToken);

    Task<Position?> FindPositionAsync(Guid tenantId, PositionId id, CancellationToken cancellationToken);

    void Add(Site site);

    void Add(Workplace workplace);

    void Add(Area area);

    void Add(Position position);

    void Remove(Site site);

    void Remove(Workplace workplace);

    void Remove(Area area);

    void Remove(Position position);

    /// <summary>Todo, archivado incluido: la pantalla pinta «(archivada)» y el historial necesita el nombre.</summary>
    Task<HrSettingsSnapshot> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<bool> IsInUseAsync(Guid tenantId, Guid settingId, CancellationToken cancellationToken);

    Task<bool> SiteHasActiveWorkplacesAsync(Guid tenantId, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// Algún cargo no archivado y algún centro no archivado, de una sede no archivada cuya empresa está en
    /// <paramref name="activeCompanyIds"/>. El repositorio no sabe qué empresa está activa: se lo dice <c>HrSetup</c>.
    /// </summary>
    Task<bool> IsSetupCompleteAsync(
        Guid tenantId, IReadOnlyCollection<Guid> activeCompanyIds, CancellationToken cancellationToken);
}
