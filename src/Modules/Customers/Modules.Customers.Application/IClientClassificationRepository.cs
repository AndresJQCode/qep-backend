using Modules.Customers.Domain;

namespace Modules.Customers.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
//
// Sin busqueda por texto ni paginacion, mismo criterio que ITaxRateRepository: una clasificacion
// de cliente por tenant se cuenta con los dedos de una mano.
public interface IClientClassificationRepository
{
    Task<IReadOnlyList<ClientClassification>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<ClientClassification?> FindAsync(
        Guid tenantId,
        ClientClassificationId classificationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="FindAsync"/>, para que <c>ListCustomersHandler</c> resuelva
    /// las clasificaciones distintas de una pagina con una sola consulta en vez de una por cliente.
    /// </summary>
    Task<IReadOnlyList<ClientClassification>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<ClientClassificationId> classificationIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Busqueda por **nombre**, case-insensitive. La usa la importacion masiva (Fase 5): el Excel
    /// trae el nombre de la clasificacion como texto, no su id.
    /// </summary>
    Task<ClientClassification?> FindByNameAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken);

    void Add(ClientClassification classification);

    // Borrado real, no logico: desactivar ya existe y es la operacion que conserva historia.
    // Este es para la clasificacion que se cargo por error y nunca se uso.
    void Remove(ClientClassification classification);
}
