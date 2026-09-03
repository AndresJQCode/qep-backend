namespace Modules.Catalog.Application;

/// <summary>
/// Arma el Excel del catalogo. Puerto y no una clase concreta por el mismo motivo que
/// <c>ICustomerImportTemplateBuilder</c>: ClosedXML es una decision de infraestructura y la
/// capa de aplicacion no deberia compilar contra ella.
/// </summary>
public interface IProductExportWorkbookBuilder
{
    byte[] Build(IReadOnlyList<ProductExportRow> products);
}

/// <summary>Un producto tal como sale al Excel, con sus escalas ya resueltas.</summary>
public sealed record ProductExportRow(
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    decimal? PriceBaseUsd,
    decimal? PriceBaseCop,
    string? TaxRateName,
    IReadOnlyList<ProductExportScale> Scales);

/// <summary>
/// Una escala del producto en la forma que necesita el export: el rango que la identifica y el
/// precio en pesos.
///
/// <paramref name="PriceCop"/> es nullable porque el producto puede tener precio solo en
/// dolares: la celda queda vacia, igual que la de un producto que no tiene esta escala. Las dos
/// ausencias se leen igual en la planilla y ninguna es un cero, que seria un precio real.
/// </summary>
public sealed record ProductExportScale(int FromUnit, int ToUnit, decimal? PriceCop);
