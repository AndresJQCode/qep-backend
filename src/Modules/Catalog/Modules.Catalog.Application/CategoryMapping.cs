using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

internal static class CategoryMapping
{
    public static CategoryDto ToDto(this ProductCategoryListItem item) =>
        item.Category.ToDto(item.ProductCount);

    public static CategoryDto ToDto(this ProductCategory category, int productCount) => new(
        category.Id.Value,
        category.Name,
        category.ParentId?.Value,
        productCount,
        category.Version,
        category.CreatedAt,
        category.UpdatedAt);
}
