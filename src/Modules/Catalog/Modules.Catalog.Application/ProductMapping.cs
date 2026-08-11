using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

internal static class ProductMapping
{
    public static ProductDto ToDto(this Product product) => new(
        product.Id.Value,
        product.Name,
        product.Code,
        product.IsActive,
        product.CreatedAt,
        product.UpdatedAt);
}
