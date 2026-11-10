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
    /// El pedido que ya salió de ese checkout (<paramref name="clientRequestId"/>), o <c>null</c>. Se mira antes de tocar al cliente:
    /// el reintento del navegador no crea ni completa nada.
    /// </summary>
    Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Crea el pedido del checkout, idempotente por <c>ClientRequestId</c>. Los errores del motor se traducen: el visitante anónimo
    /// nunca ve <c>quotation.*</c>, sólo <c>store.order_request.line_unavailable</c>.
    /// </summary>
    Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken);

    /// <summary>Cuántos pedidos de la tienda entraron desde <paramref name="since"/>, anulados incluidos: el tope diario.</summary>
    Task<int> CountCreatedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);
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

/// <summary><c>ClientRequestId</c> es el id que el navegador genera por checkout: hace idempotente el pedido. No hay persona detrás:
/// el pedido lo crea el sistema. <c>BuyerAccountId</c> es la cuenta de comprador de una sesión válida en el checkout; null =
/// invitado.</summary>
public sealed record StoreOrderDraft(
    Guid TenantId,
    Guid ClientRequestId,
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
    Guid? BuyerAccountId = null);

/// <summary>Con <c>AlreadyExisted</c> el resto describe el pedido que ya estaba (cliente y asesor incluidos).</summary>
public sealed record StoreOrderCreated(
    Guid OrderId,
    string OrderNumber,
    decimal Total,
    string Currency,
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
