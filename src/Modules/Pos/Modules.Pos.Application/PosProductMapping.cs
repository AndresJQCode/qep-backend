namespace Modules.Pos.Application;

internal static class PosProductMapping
{
    public const string Inactive = "Inactive";
    public const string PriceMissing = "PriceMissing";
    public const string NotFound = "NotFound";

    /// <summary>
    /// The list that matches the session, never a fallback to the other one: selling a USD session
    /// at the peso price would be a silent 4000x error.
    /// </summary>
    internal static decimal? PriceIn(this PosProductRef product, string currency) =>
        currency == "USD" ? product.PriceUsd : product.PriceCop;

    public static string? UnsellableReason(PosProductRef product, string currency) =>
        !product.IsActive ? Inactive
        : product.PriceIn(currency) is null ? PriceMissing
        : null;

    public static PosProductResponse ToResponse(PosProductRef product, string currency)
    {
        var reason = UnsellableReason(product, currency);
        return new PosProductResponse(
            product.Id, product.Code, product.Name, product.PriceIn(currency), product.TaxPercentage,
            product.ImageUrl, reason is null, reason);
    }
}
