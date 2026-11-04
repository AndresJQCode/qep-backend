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

    /// <summary>
    /// El pedido que ya salió de esa solicitud, o <c>null</c>. «Tomar» lo mira antes de tocar al cliente: tras una caída entre crear el
    /// pedido y marcar la solicitud, el reintento no crea ni completa nada.
    /// </summary>
    Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Crea el pedido de la solicitud, idempotente por la solicitud. Los errores del motor (<c>quotation.item.product_inactive</c>,
    /// <c>quotation.item.product_price_unavailable</c>) suben tal cual: aquí sí los ve el asesor.
    /// </summary>
    Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken);
}

/// <summary>Una parte del pedido. En <c>CO</c> van los ids y <c>CityName</c> es null; afuera sólo <c>CityName</c>.</summary>
public sealed record StoreOrderPartyInput(
    string Name,
    string Phone,
    string Email,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName,
    string? IdentificationType,
    string? IdentificationNumber);

public sealed record StoreOrderLineInput(Guid VariantId, decimal Quantity);

/// <summary><c>TakerUserId</c> es la persona que toma la solicitud: firma la auditoría del pedido, que no tiene otro actor humano.</summary>
public sealed record StoreOrderDraft(
    Guid TenantId,
    Guid RequestId,
    string RequestNumber,
    Guid ClientId,
    Guid AdvisorMembershipId,
    bool IsRetail,
    StoreBillingAccount BillingAccount,
    StoreOrderPartyInput Billing,
    StoreOrderPartyInput Shipping,
    bool BillingWithRetention,
    bool BillingVatSurplus,
    IReadOnlyList<StoreOrderLineInput> Lines,
    string? Notes,
    Guid TakerUserId);

/// <summary>Con <c>AlreadyExisted</c> el resto describe el pedido que ya estaba (cliente y asesor incluidos).</summary>
public sealed record StoreOrderCreated(
    Guid OrderId,
    string OrderNumber,
    decimal Total,
    Guid ClientId,
    Guid AdvisorMembershipId,
    bool AlreadyExisted);

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
