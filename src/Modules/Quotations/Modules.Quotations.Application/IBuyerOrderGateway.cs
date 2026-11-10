using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una línea del pedido como la ve el comprador. <c>LineTotal</c> es <c>Subtotal + TaxAmount</c>: lo que la
/// línea suma al total.</summary>
public sealed record BuyerOrderLine(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record BuyerOrderSummary(
    Guid Id,
    string Number,
    DateTimeOffset CreatedAt,
    string Status,
    string PaymentStatus,
    decimal Total,
    string Currency,
    int LineCount,
    string FirstLineName,
    bool CanCancel);

public sealed record BuyerOrderDetail(
    Guid Id,
    string Number,
    DateTimeOffset CreatedAt,
    string Status,
    string PaymentStatus,
    decimal Total,
    string Currency,
    int LineCount,
    string FirstLineName,
    IReadOnlyList<BuyerOrderLine> Lines,
    DateTimeOffset? CancelledAt,
    bool CanCancel);

public sealed record BuyerOrderPage(IReadOnlyList<BuyerOrderSummary> Items, int Page, int PageSize, int Total);

/// <summary>El puerto de la cuenta del comprador hacia sus pedidos de la tienda. Todo se filtra por tenant y
/// comprador: el pedido de otro comprador no existe para éste.</summary>
public interface IBuyerOrderGateway
{
    Task<BuyerOrderPage> ListAsync(
        Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken);

    Task<BuyerOrderDetail?> FindAsync(
        Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Anula el pedido. <c>null</c> si no existe para ese comprador; 409
    /// <see cref="ResourceConflictException"/> con <see cref="BuyerOrderErrors.NotCancellableByBuyer"/> si ya no es
    /// anulable, también cuando pierde la carrera contra otra edición.</summary>
    Task<BuyerOrderDetail?> CancelAsync(
        Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Pone el comprador sólo si el pedido no tiene uno. Devuelve <c>true</c> si escribió.</summary>
    Task<bool> LinkAsync(Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken);
}

public sealed class BuyerOrderGateway(
    IOrderRepository orders,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IClock clock)
    : IBuyerOrderGateway
{
    public async Task<BuyerOrderPage> ListAsync(
        Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (items, total) = await orders.SearchByBuyerAsync(tenantId, buyerAccountId, page, pageSize, cancellationToken);
        return new BuyerOrderPage(items.Select(ToSummary).ToArray(), page, pageSize, total);
    }

    public async Task<BuyerOrderDetail?> FindAsync(
        Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken) =>
        await FindOwnedAsync(tenantId, buyerAccountId, new OrderId(orderId), cancellationToken) is { } order
            ? ToDetail(order)
            : null;

    public async Task<BuyerOrderDetail?> CancelAsync(
        Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken)
    {
        var id = new OrderId(orderId);
        var order = await FindOwnedAsync(tenantId, buyerAccountId, id, cancellationToken);
        if (order is null)
        {
            return null;
        }

        try
        {
            await CancelAndSaveAsync(tenantId, buyerAccountId, order, cancellationToken);
            return ToDetail(order);
        }
        catch (RequestConcurrencyException)
        {
            // Otra escritura ganó. Lo pendiente (anulación, historial, auditoría, evento) se descarta y se decide de
            // nuevo sobre el pedido como quedó: si sigue anulable, un reintento; si no, el mismo 409.
            unitOfWork.DiscardPendingChanges();
        }

        var reread = await FindOwnedAsync(tenantId, buyerAccountId, id, cancellationToken);
        if (reread is null)
        {
            return null;
        }

        try
        {
            await CancelAndSaveAsync(tenantId, buyerAccountId, reread, cancellationToken);
            return ToDetail(reread);
        }
        catch (RequestConcurrencyException exception)
        {
            // La tienda nunca ve un 412: no tiene versión que mandar ni pantalla que recargar.
            unitOfWork.DiscardPendingChanges();
            throw NotCancellable(exception);
        }
    }

    public async Task<bool> LinkAsync(
        Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken)
    {
        var order = await orders.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        if (order is null || !order.LinkBuyerAccount(buyerAccountId))
        {
            return false;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task CancelAndSaveAsync(
        Guid tenantId, Guid buyerAccountId, Order order, CancellationToken cancellationToken)
    {
        // La regla se mira acá para responder 409; la del dominio queda como defensa.
        if (!order.CanBeCancelledByBuyer)
        {
            throw NotCancellable();
        }

        var now = clock.UtcNow;
        try
        {
            order.CancelByBuyer(buyerAccountId, now);
        }
        catch (QuotationsDomainException exception) when (exception.Code == BuyerOrderErrors.NotCancellableByBuyer)
        {
            throw NotCancellable(exception);
        }

        orders.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(),
            order.Id,
            OrderHistoryEventType.Cancelled,
            memberId: null,
            OrderChangeSummary.Cancelled(order.CancellationReason!),
            now));
        auditPublisher.PublishByIntegration(tenantId, "quotation.order.cancelled", order.Id.ToString(), "success", now);
        lifecycleEvents.PublishOrderCancelled(order, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // El repositorio ya filtra por tenant y comprador; se vuelve a mirar para que un repositorio que no lo hiciera
    // nunca entregue el pedido de otro.
    private async Task<Order?> FindOwnedAsync(
        Guid tenantId, Guid buyerAccountId, OrderId orderId, CancellationToken cancellationToken) =>
        await orders.FindByBuyerAsync(tenantId, buyerAccountId, orderId, cancellationToken) is { } order
            && order.TenantId == tenantId
            && order.BuyerAccountId == buyerAccountId
            ? order
            : null;

    private static ResourceConflictException NotCancellable(Exception? innerException = null) =>
        new(BuyerOrderErrors.NotCancellableByBuyer, "The order can no longer be cancelled from the store.", innerException);

    private static BuyerOrderSummary ToSummary(Order order) =>
        new(
            order.Id.Value,
            order.OrderNumber,
            order.CreatedAt,
            order.Status.ToString(),
            order.PaymentStatus.ToString(),
            order.Total,
            order.Currency,
            order.Items.Count,
            FirstLineName(order),
            order.CanBeCancelledByBuyer);

    private static BuyerOrderDetail ToDetail(Order order) =>
        new(
            order.Id.Value,
            order.OrderNumber,
            order.CreatedAt,
            order.Status.ToString(),
            order.PaymentStatus.ToString(),
            order.Total,
            order.Currency,
            order.Items.Count,
            FirstLineName(order),
            order.Items
                .OrderBy(item => item.Position)
                .Select(item => new BuyerOrderLine(
                    item.ProductName ?? string.Empty, item.Quantity, item.UnitPrice, item.Subtotal + item.TaxAmount))
                .ToArray(),
            order.CancelledAt,
            order.CanBeCancelledByBuyer);

    private static string FirstLineName(Order order) =>
        order.Items.OrderBy(item => item.Position).FirstOrDefault()?.ProductName ?? string.Empty;
}
