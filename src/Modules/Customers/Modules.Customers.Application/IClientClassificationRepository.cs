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

    void Add(ClientClassification classification);

    // Borrado real, no logico: desactivar ya existe y es la operacion que conserva historia.
    // Este es para la clasificacion que se cargo por error y nunca se uso.
    void Remove(ClientClassification classification);
}
