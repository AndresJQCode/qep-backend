using Modules.Catalog.Application;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Store. En la parte de ajustes sólo traduce: los nombres salen del árbol de categorías del tenant (el mismo que arma la
/// tienda pública) y las monedas con precio, de <see cref="ICurrenciesInUse"/>, la fuente de <c>GET /catalog/currencies-in-use</c>.
/// </summary>
internal sealed class StoreCatalogReader(IProductCategoryRepository categories, ICurrenciesInUse currenciesInUse)
    : IStoreCatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // La consulta ya va acotada al tenant: un id ajeno o borrado simplemente no aparece.
        var wanted = ids.ToHashSet();
        return (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .Where(node => wanted.Contains(node.Id.Value))
            .ToDictionary(node => node.Id.Value, node => node.Name);
    }

    public async Task<IReadOnlyCollection<string>> CurrenciesInUseAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await currenciesInUse.GetAsync(tenantId, cancellationToken);
}
