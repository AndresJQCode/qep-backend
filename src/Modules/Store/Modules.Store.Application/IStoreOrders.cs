using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <summary>
/// El motor de pedidos visto desde la tienda. Los totales salen siempre de acá: el navegador nunca calcula un precio, una escala ni un
/// total. Una línea que el motor rechaza (producto inexistente, inactivo, cantidad que la escala no admite) sale como
/// <c>store.order_request.line_unavailable</c>.
/// </summary>
public interface IStoreOrders
{
    Task<StoreQuoteView> QuoteAsync(
        Guid tenantId,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<(Guid VariantId, int Quantity)> lines,
        CancellationToken cancellationToken);
}

public sealed record StoreQuoteLineView(
    Guid VariantId,
    Guid ProductId,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountedUnitPrice,
    decimal LineTotal,
    string DiscountOrigin = "");

public sealed record StoreQuoteView(
    string Currency,
    IReadOnlyList<StoreQuoteLineView> Lines,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Total);
