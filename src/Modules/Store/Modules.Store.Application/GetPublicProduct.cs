using BuildingBlocks.Application;
using Modules.Store.Domain;

namespace Modules.Store.Application;

public sealed record GetPublicProductQuery(string Host, Guid ProductId, string? Currency) : IQuery<PublicProductDto>;

public sealed class GetPublicProductHandler(StorefrontResolver resolver, IStoreCatalogReader catalog)
    : IQueryHandler<GetPublicProductQuery, PublicProductDto>
{
    public async Task<PublicProductDto> HandleAsync(GetPublicProductQuery query, CancellationToken cancellationToken)
    {
        var (store, _) = await resolver.ResolveAsync(query.Host, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, query.Currency);

        // «Sólo detal» no muestra escalas: el catálogo ni las lee. Cuántas hay y de cuánto es cada una no es asunto del visitante.
        var includeScales = store.PricingMode == StorePricingMode.QuantityScales;
        var product = await catalog.ProductAsync(store.TenantId, query.ProductId, currency, includeScales, cancellationToken)
            ?? throw new ResourceNotFoundException(StoreErrorCodes.PublicProductNotFound, "The product was not found.");

        return new PublicProductDto(
            product.Id,
            product.Name,
            product.Description,
            product.CategoryId,
            product.ImageUrl,
            product.Gallery,
            currency,
            product.Variants.Select(variant => new PublicVariantDto(
                variant.Id,
                variant.Name,
                variant.ImageUrl,
                variant.Price,
                variant.PackagingUnits,
                variant.Scales.Select(scale => new PublicScaleDto(
                    scale.FromUnit,
                    scale.ToUnit,
                    scale.Discount,
                    scale.Final,
                    scale.Restriction,
                    scale.Multiple,
                    scale.AllowGrouping)).ToList())).ToList());
    }
}
