using BuildingBlocks.Application;

namespace Modules.Catalog.Application;

// The lookup is always scoped to the caller tenant, so "not found" here means "not found in
// your catalogue". A product of another tenant is unreachable earlier, at the authorization
// check, and answers 403 — never 404, which would confirm the id exists somewhere.
internal static class ProductNotFound
{
    public static ResourceNotFoundException For(Guid productId) =>
        new("catalog.product.not_found", $"Product '{productId}' was not found.");
}
