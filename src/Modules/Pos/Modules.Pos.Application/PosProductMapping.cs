namespace Modules.Pos.Application;

internal static class PosProductMapping
{
    public const string Inactive = "Inactive";
    public const string PriceMissing = "PriceMissing";
    public const string NotFound = "NotFound";

    public static string? UnsellableReason(PosProductRef product) =>
        !product.IsActive ? Inactive
        : product.PriceCop is null ? PriceMissing
        : null;

    public static PosProductResponse ToResponse(PosProductRef product)
    {
        var reason = UnsellableReason(product);
        return new PosProductResponse(
            product.Id, product.Code, product.Name, product.PriceCop, product.TaxPercentage,
            product.ImageUrl, reason is null, reason);
    }
}
