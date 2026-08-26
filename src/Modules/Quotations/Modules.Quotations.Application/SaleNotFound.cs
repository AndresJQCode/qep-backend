using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

// La busqueda siempre esta acotada al tenant del llamador: "no encontrada" acá significa "no
// encontrada en tu tenant". Mismo criterio que QuotationNotFound.
internal static class SaleNotFound
{
    public static ResourceNotFoundException For(Guid quotationId) =>
        new("sale.sale.not_found", $"Sale for quotation '{quotationId}' was not found.");
}
