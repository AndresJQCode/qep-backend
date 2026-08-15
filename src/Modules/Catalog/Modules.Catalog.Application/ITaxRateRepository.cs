using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
//
// Sin búsqueda por texto, a diferencia de IProductRepository: una tasa de impuesto por tenant se
// cuenta con los dedos de una mano, así que un filtro no resuelve un problema que exista. Se
// agrega el día que alguien lo pida con un caso.
public interface ITaxRateRepository
{
    Task<IReadOnlyList<TaxRate>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<TaxRate?> FindAsync(
        Guid tenantId,
        TaxRateId taxRateId,
        CancellationToken cancellationToken);

    void Add(TaxRate taxRate);

    // Borrado real, no lógico: desactivar ya existe y es la operación que conserva historia.
    // Éste es para la tasa que se cargó por error y nunca se usó. CAT-06.
    void Remove(TaxRate taxRate);
}
