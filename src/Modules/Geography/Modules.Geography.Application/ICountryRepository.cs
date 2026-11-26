using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public interface ICountryRepository
{
    /// <summary>Geography no tiene tenant: a diferencia del resto de los repositorios, no lleva <c>tenantId</c>.</summary>
    Task<IReadOnlyList<Country>> ListAllAsync(CancellationToken cancellationToken);
}
