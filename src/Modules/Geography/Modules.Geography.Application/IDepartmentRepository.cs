using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public interface IDepartmentRepository
{
    Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Busqueda puntual por id. Geography no tiene tenant: a diferencia del resto de los
    /// repositorios de este repo, no lleva <c>tenantId</c>. La usa <c>CustomerGeographyLookup</c>
    /// (en <c>Bootstrapper</c>) para resolver el departamento de la ciudad de un cliente.
    /// </summary>
    Task<Department?> FindAsync(DepartmentId departmentId, CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="FindAsync"/>, para resolver varios departamentos con una
    /// sola consulta en vez de una por id — la necesita el listado de clientes.
    /// </summary>
    Task<IReadOnlyList<Department>> ListByIdsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken);

    /// <summary>
    /// Busqueda por **nombre**, case-insensitive y sin espacios sobrantes. La usa
    /// <c>CustomerGeographyLookup</c> para resolver el departamento de texto que trae el Excel de
    /// importacion masiva de clientes.
    /// </summary>
    Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken);
}
