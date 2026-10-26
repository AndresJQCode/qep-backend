using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// One place for the two codes: DeleteCategoryHandler throws them before the delete, and
// CatalogUnitOfWork throws the same ones when a RESTRICT FK catches the race. Two copies of the
// text would drift.
public static class CategoryInUse
{
    public static CatalogDomainException HasProducts() =>
        new(
            "catalog.category.has_products",
            "The category cannot be deleted because at least one product uses it.");

    public static CatalogDomainException HasChildren() =>
        new(
            "catalog.category.has_children",
            "The category cannot be deleted because it has child categories.");
}
