using BuildingBlocks.Application;

namespace Modules.Catalog.Application;

// Same reasoning as TaxRateNotFound: the lookup is always scoped to the caller's tenant, so a
// category of another tenant is "not found here", never a confirmation that the id exists.
internal static class CategoryNotFound
{
    public static ResourceNotFoundException For(Guid categoryId) =>
        new("catalog.category.not_found", $"Category '{categoryId}' was not found.");
}
