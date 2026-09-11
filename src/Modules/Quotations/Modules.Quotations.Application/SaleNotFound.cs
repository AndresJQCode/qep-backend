using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

// La busqueda siempre esta acotada al tenant del llamador: "no encontrada" acá significa "no
// encontrada en tu tenant". Mismo criterio que QuotationNotFound.
internal static class SaleNotFound
{
    public static ResourceNotFoundException For(Guid quotationId) =>
        new("sale.sale.not_found", $"Sale for quotation '{quotationId}' was not found.");

    /// <summary>Cuando se entra por el id de la venta y no por el de su cotizacion (SALE-04).
    /// Mismo codigo: para quien consume la API es la misma venta que no aparece.</summary>
    public static ResourceNotFoundException ById(Guid saleId) =>
        new("sale.sale.not_found", $"Sale '{saleId}' was not found.");
}
