using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Builds the catalogue Excel. A port and not a concrete class for the same reason as
/// <c>ICustomerImportTemplateBuilder</c>: ClosedXML is an infrastructure decision and the
/// application layer should not compile against it.
/// </summary>
public interface IProductExportWorkbookBuilder
{
    /// <param name="currencies">The tenant's currencies in use (spec D10), catalogue order. One
    /// base-price column, and one final column per scale, for each; its decimals pick the format.</param>
    byte[] Build(IReadOnlyList<ProductExportRow> products, IReadOnlyList<CurrencyInfo> currencies);
}

/// <summary>A product as it goes into the Excel, with its scales already resolved.</summary>
public sealed record ProductExportRow(
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> Prices,
    string? TaxRateName,
    IReadOnlyList<ProductExportScale> Scales);

/// <summary>
/// A product scale in the shape the export needs: the range that identifies it and its discount.
/// The discount only: the builder derives the final per currency with PriceScale.FinalFor (spec D4).
/// </summary>
public sealed record ProductExportScale(int FromUnit, int ToUnit, decimal DiscountPercent);
