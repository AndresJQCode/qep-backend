using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una línea del pedido como la ve el comprador. <c>LineTotal</c> es <c>Subtotal + TaxAmount</c>: lo que la
/// línea suma al total. Con excedente de IVA el IVA no se cobra aparte, así que la línea va sin IVA
/// (<c>UnitPriceWithoutTax</c> y <c>Subtotal</c>) para que las líneas, con el flete, sumen el total (spec §4.7). No
/// se «simplifica» a una sola fórmula: sin excedente son otros números.</summary>
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

/// <summary>A quién se le factura, cuando tiene datos propios. <c>Name</c> no es nulo porque esta parte sólo existe
/// con <see cref="BilledPartySource.BillingParty"/>, que exige nombre. En Colombia los nombres salen de Geography; fuera,
/// sólo la ciudad como texto.</summary>
public sealed record BuyerOrderBilling(
    string Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Email,
    string? Phone,
    string? Address,
    string Country,
    string? DepartmentName,
    string? CityName);

/// <summary>A dónde se entrega. Sin correo ni identificación: la parte de entrega no los lleva.</summary>
public sealed record BuyerOrderShipping(
    string? Name,
    string? Phone,
    string? Address,
    string Country,
    string? DepartmentName,
    string? CityName);

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
    bool CanCancel,
    decimal Subtotal,
    decimal TaxAmount,
    decimal FreightTotal,
    bool IsStorePickup,
    string? CarrierName,
    string BilledTo,
    BuyerOrderBilling? Billing,
    BuyerOrderShipping? Shipping,
    string? BuyerNotes);

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

    /// <summary>Pone el comprador sólo si el pedido no tiene uno. Devuelve <c>true</c> si escribió. Si pierde la
    /// carrera contra otra edición relee y lo intenta una vez más; la segunda <see cref="RequestConcurrencyException"/>
    /// sale tal cual.</summary>
    Task<bool> LinkAsync(Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken);
}

public sealed class BuyerOrderGateway(
    IOrderRepository orders,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IClock clock,
    IQuotationGeographyLookup geography)
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
            ? ToDetail(order, await ResolvePlacesAsync(order, cancellationToken))
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
            // Los nombres se resuelven antes de anular: si el lookup falla, el pedido queda intacto.
            var places = await ResolvePlacesIfCancellableAsync(order, cancellationToken);
            await CancelAndSaveAsync(tenantId, buyerAccountId, order, cancellationToken);
            return ToDetail(order, places);
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
            // Del pedido que se anula, no del primero: el releído puede traer ediciones del asesor.
            var places = await ResolvePlacesIfCancellableAsync(reread, cancellationToken);
            await CancelAndSaveAsync(tenantId, buyerAccountId, reread, cancellationToken);
            return ToDetail(reread, places);
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
        try
        {
            return await LinkAndSaveAsync(tenantId, orderId, buyerAccountId, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            // El enlace no sube la versión, pero la versión es token de concurrencia: otra escritura que la subió entre
            // la lectura y el guardado gana. Se relee el pedido como quedó y se decide de nuevo (sólo si sigue vacío).
            unitOfWork.DiscardPendingChanges();
        }

        try
        {
            return await LinkAndSaveAsync(tenantId, orderId, buyerAccountId, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            unitOfWork.DiscardPendingChanges();
            throw;
        }
    }

    private async Task<bool> LinkAndSaveAsync(
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

    private sealed record Places(
        IReadOnlyDictionary<Guid, string> Departments, IReadOnlyDictionary<Guid, string> Cities);

    // Sólo se muestra la parte que corresponde; la facturación que no se muestra no se consulta.
    private static (BilledPartySource BilledTo, OrderParty? Billing, OrderParty? Shipping) PartiesShown(Order order)
    {
        var billedTo = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing);
        return (
            billedTo,
            billedTo == BilledPartySource.BillingParty ? order.Billing : null,
            order.IsStorePickup ? null : order.Shipping);
    }

    // Un pedido que ya no se puede anular responde 409 sin tocar Geography: CancelAndSaveAsync lo rechaza.
    private async Task<Places> ResolvePlacesIfCancellableAsync(Order order, CancellationToken cancellationToken) =>
        order.CanBeCancelledByBuyer
            ? await ResolvePlacesAsync(order, cancellationToken)
            : new Places(new Dictionary<Guid, string>(), new Dictionary<Guid, string>());

    private async Task<Places> ResolvePlacesAsync(Order order, CancellationToken cancellationToken)
    {
        var (_, billingParty, shippingParty) = PartiesShown(order);

        // Un diccionario por llamada y sólo si hay ids: facturación y envío en la misma ciudad piden una vez. Una
        // excepción del lookup sale tal cual; nunca se disfraza de nombre nulo.
        var colombian = new[] { billingParty, shippingParty }
            .Where(party => party is { Country: null })
            .ToArray();
        var departments = await NamesAsync(
            colombian.Select(party => party!.DepartmentId), geography.FindDepartmentNamesAsync, cancellationToken);
        var cities = await NamesAsync(
            colombian.Select(party => party!.CityId), geography.FindCityNamesAsync, cancellationToken);
        return new Places(departments, cities);
    }

    private static BuyerOrderDetail ToDetail(Order order, Places places)
    {
        var (billedTo, billingParty, shippingParty) = PartiesShown(order);
        var departments = places.Departments;
        var cities = places.Cities;

        var appliesVatSurplus = order.AppliesVatSurplus;
        return new BuyerOrderDetail(
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
                .Select(item => ToLine(item, appliesVatSurplus))
                .ToArray(),
            order.CancelledAt,
            order.CanBeCancelledByBuyer,
            order.Subtotal,
            order.TaxAmount,
            order.FreightSubtotal + order.FreightTaxAmount,
            order.IsStorePickup,
            order.CarrierName,
            billedTo.ToString(),
            billingParty is null
                ? null
                : new BuyerOrderBilling(
                    billingParty.Name ?? string.Empty,
                    billingParty.IdentificationType?.ToWireValue(),
                    billingParty.IdentificationNumber,
                    billingParty.Email,
                    billingParty.Phone,
                    billingParty.Address,
                    CountryOf(billingParty),
                    DepartmentName(billingParty, departments),
                    CityName(billingParty, cities)),
            shippingParty is null
                ? null
                : new BuyerOrderShipping(
                    shippingParty.Name,
                    shippingParty.Phone,
                    shippingParty.Address,
                    CountryOf(shippingParty),
                    DepartmentName(shippingParty, departments),
                    CityName(shippingParty, cities)),
            order.BuyerNotes);
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        IEnumerable<Guid?> ids,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> find,
        CancellationToken cancellationToken)
    {
        var distinct = ids.OfType<Guid>().Distinct().ToArray();
        return distinct.Length == 0
            ? new Dictionary<Guid, string>()
            : await find(distinct, cancellationToken);
    }

    private static string CountryOf(OrderParty party) => party.Country ?? QuotationPartyDetails.ColombiaCountryCode;

    private static string? DepartmentName(OrderParty party, IReadOnlyDictionary<Guid, string> departments) =>
        party is { Country: null, DepartmentId: { } id } && departments.TryGetValue(id, out var name) ? name : null;

    private static string? CityName(OrderParty party, IReadOnlyDictionary<Guid, string> cities) =>
        party.Country is not null
            ? party.CityName
            : party.CityId is { } id && cities.TryGetValue(id, out var name) ? name : null;

    private static BuyerOrderLine ToLine(OrderItem item, bool appliesVatSurplus) =>
        appliesVatSurplus
            ? new BuyerOrderLine(item.ProductName ?? string.Empty, item.Quantity, item.UnitPriceWithoutTax, item.Subtotal)
            : new BuyerOrderLine(
                item.ProductName ?? string.Empty, item.Quantity, item.UnitPrice, item.Subtotal + item.TaxAmount);

    private static string FirstLineName(Order order) =>
        order.Items.OrderBy(item => item.Position).FirstOrDefault()?.ProductName ?? string.Empty;
}
