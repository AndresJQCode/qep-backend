using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Resuelve los <c>priceListId</c> de un lote de escalas contra las listas **del tenant del
/// producto**, y de paso trae el nombre para la respuesta — una sola consulta al puerto por
/// escritura, en vez de una por escala.
///
/// Existe por la misma razón que <see cref="ProductImageResolver"/>, con el mismo agravante:
/// <c>PriceListId</c> es referencia **blanda, sin FK** —no puede tenerla, cruzaría a otro
/// módulo—, así que no hay ninguna red debajo de esta comprobación. A diferencia de la imagen,
/// acá el campo es obligatorio: toda escala necesita una lista válida y activa, no sólo las que
/// la traen.
/// </summary>
internal static class ProductPriceListResolver
{
    public static async Task<IReadOnlyDictionary<Guid, CatalogPriceListRef>> ResolveAsync(
        ICatalogPriceListLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<PriceScaleInput> scales,
        CancellationToken cancellationToken)
    {
        var priceListIds = scales.Select(scale => scale.PriceListId).Distinct().ToArray();
        if (priceListIds.Length == 0)
        {
            return new Dictionary<Guid, CatalogPriceListRef>();
        }

        var found = await lookup.ListByIdsAsync(priceListIds, cancellationToken);

        foreach (var priceListId in priceListIds)
        {
            // Las dos condiciones dan el mismo código a propósito: distinguir "no existe" de "es
            // de otro tenant" le confirma al llamador que el id existe en otro tenant, que es
            // justo lo que la frontera esconde. Mismo razonamiento que ProductTaxRateResolver.
            if (!found.TryGetValue(priceListId, out var priceList) || priceList.TenantId != tenantId)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.price_list_not_found",
                    "The price list was not found in this tenant.");
            }

            if (!priceList.IsActive)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.price_list_inactive",
                    "An inactive price list cannot be used for a new price scale.");
            }
        }

        return found;
    }
}
