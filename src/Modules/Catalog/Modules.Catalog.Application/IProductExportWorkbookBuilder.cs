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
    byte[] Build(IReadOnlyList<ProductExportRow> rows, IReadOnlyList<CurrencyInfo> currencies);
}

/// <summary>
/// A **variant** as it goes into the Excel (spec 2026-10-20 §7): one row per variant, with its
/// scales already resolved. The rows of a product come together and in the variants' order; the
/// product's own fields (name, description, tax rate) repeat on each of its rows so a filtered or
/// sorted spreadsheet never loses them.
/// </summary>
/// <param name="ProductName">The product's name, as is.</param>
/// <param name="VariantName">The variant's visible name (<c>VariantDisplayName</c>) when it is told
/// apart by options; null for a product's default variant, whose name is already
/// <paramref name="ProductName"/>: repeating it would double every row of a catalogue that has no
/// options yet.</param>
/// <param name="IsActive">Whether the row can be sold: the product <b>and</b> the variant are active.</param>
public sealed record ProductExportRow(
    string Code,
    string? Barcode,
    string ProductName,
    string? VariantName,
    string? Description,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> Prices,
    string? TaxRateName,
    IReadOnlyList<ProductExportScale> Scales);

/// <summary>
/// A variant scale in the shape the export needs: the range that identifies it and its discount.
/// The discount only: the builder derives the final per currency with PriceScale.FinalFor (spec D4).
/// </summary>
public sealed record ProductExportScale(int FromUnit, int ToUnit, decimal DiscountPercent);
