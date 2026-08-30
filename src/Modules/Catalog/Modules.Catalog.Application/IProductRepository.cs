using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca
// un argumento opcional que el llamador se pueda olvidar.
public interface IProductRepository
{
    /// <summary>
    /// Una página del listado y el total que la acompaña — mismo contrato que
    /// `ICustomerRepository.SearchAsync`. `name`/`code` son dos filtros independientes (AND
    /// cuando se llenan los dos); `search` es el criterio OR original sobre los mismos dos
    /// campos, para el combobox de productos de `quotes`. `isActive` es un filtro real de la
    /// consulta, no un post-filtro en memoria.
    /// </summary>
    Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cuáles de estos códigos ya existen en el tenant, en una sola consulta — la usa la
    /// importación masiva para el chequeo de duplicados **contra la base**, mismo criterio que
    /// <c>ICustomerRepository.FindExistingIdentificationsAsync</c>: una consulta batch en vez de
    /// una por fila.
    /// </summary>
    Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken);

    void Add(Product product);

    /// <summary>
    /// Si algún producto del tenant apunta a esa tasa. Lo pregunta `DeleteTaxRate` antes de
    /// borrar, para que el caso normal salga como un 422 que se entiende en vez de una violación
    /// de foreign key traducida.
    ///
    /// Devuelve un booleano y no la lista: quien pregunta sólo necesita saber si puede borrar, y
    /// traer los productos para contarlos sería traer datos que nadie va a mirar.
    /// </summary>
    Task<bool> AnyWithTaxRateAsync(
        Guid tenantId,
        TaxRateId taxRateId,
        CancellationToken cancellationToken);
}
