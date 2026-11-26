using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Regiones y municipios de los países fuera de Colombia. Geography no tiene tenant: a diferencia
/// del resto de los repositorios, no lleva <c>tenantId</c>. Colombia no pasa por acá, ver
/// <see cref="IDepartmentRepository"/> e <see cref="ICityRepository"/>.
/// </summary>
public interface IRegionRepository
{
    Task<IReadOnlyList<Region>> ListByCountryAsync(string countryCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<Region>> ListByIdsAsync(
        IReadOnlyCollection<RegionId> regionIds, CancellationToken cancellationToken);

    Task<Region?> FindAsync(RegionId regionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegionCity>> ListCitiesAsync(RegionId regionId, CancellationToken cancellationToken);
}
