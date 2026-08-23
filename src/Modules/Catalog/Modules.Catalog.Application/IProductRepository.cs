using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca
// un argumento opcional que el llamador se pueda olvidar.
public interface IProductRepository
{
    Task<IReadOnlyList<Product>> SearchAsync(
        Guid tenantId,
        string? search,
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
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

    /// <summary>
    /// Si alguna escala de precio de algún producto del tenant referencia esa lista de precios.
    /// La usa <c>IPriceListUsageLookup</c> (adaptado en Bootstrapper) para que
    /// <c>DeletePriceList</c> responda un 422 legible antes de dejar escalas huérfanas — mismo
    /// patrón que <see cref="AnyWithTaxRateAsync"/>.
    /// </summary>
    Task<bool> AnyWithPriceListAsync(
        Guid tenantId,
        Guid priceListId,
        CancellationToken cancellationToken);
}
