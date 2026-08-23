using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public interface ICityRepository
{
    Task<IReadOnlyList<City>> ListByDepartmentAsync(
        DepartmentId departmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Busqueda puntual por id. Geography no tiene tenant: a diferencia del resto de los
    /// repositorios de este repo, no lleva <c>tenantId</c>. La usa <c>CustomerGeographyLookup</c>
    /// (en <c>Bootstrapper</c>) para resolver la ciudad de un cliente al crearlo, editarlo o
    /// devolverlo.
    /// </summary>
    Task<City?> FindAsync(CityId cityId, CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="FindAsync"/>: la usa el listado de clientes para resolver
    /// las ciudades distintas de una pagina con una sola consulta en vez de una por cliente.
    /// </summary>
    Task<IReadOnlyList<City>> ListByIdsAsync(
        IReadOnlyCollection<CityId> cityIds, CancellationToken cancellationToken);

    /// <summary>
    /// Busqueda por **nombre dentro de un departamento**, case-insensitive y sin espacios
    /// sobrantes — nunca por nombre de ciudad solo: el mismo nombre puede repetirse en mas de un
    /// departamento del DIVIPOLA, y buscarlo aislado seria ambiguo. La usa
    /// <c>CustomerGeographyLookup</c> para la importacion masiva de clientes.
    /// </summary>
    Task<City?> FindByNameAsync(
        DepartmentId departmentId, string name, CancellationToken cancellationToken);
}
