using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

// La busqueda siempre esta acotada al tenant del llamador: "no encontrada" acá significa "no
// encontrada en tu tenant". Mismo criterio que QuotationNotFound.
internal static class OrderNotFound
{
    public static ResourceNotFoundException For(Guid quotationId) =>
        new("order.order.not_found", $"Order for quotation '{quotationId}' was not found.");

    /// <summary>Cuando se entra por el id del pedido y no por el de su cotizacion (SALE-04).
    /// Mismo codigo: para quien consume la API es el mismo pedido que no aparece.</summary>
    public static ResourceNotFoundException ById(Guid orderId) =>
        new("order.order.not_found", $"Order '{orderId}' was not found.");
}
