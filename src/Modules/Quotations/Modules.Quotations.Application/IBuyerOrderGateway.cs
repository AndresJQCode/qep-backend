using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una línea del pedido como la ve el comprador. <c>LineTotal</c> es <c>Subtotal + TaxAmount</c>: lo que la
/// línea suma al total. Con excedente de IVA el IVA no se cobra aparte, así que la línea va sin IVA
/// (<c>UnitPriceWithoutTax</c> y <c>Subtotal</c>) para que las líneas, con el flete, sumen el total (spec §4.7). No
/// se «simplifica» a una sola fórmula: sin excedente son otros números. <c>ImageUrl</c> es la foto <b>actual</b> del
/// catálogo (la de la variante o, sin ella, la principal del producto), no una copia del momento de la compra (owner,
/// 2026-11-15); null si la variante ya no está o no tiene foto.</summary>
public sealed record BuyerOrderLine(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal, string? ImageUrl);

/// <summary>Los cuatro ejes del pedido (spec 2026-11-10 §9), por nombre: <c>Status</c> es <c>Open</c>,
/// <c>Archived</c> o <c>Cancelled</c>; <c>FulfillmentStatus</c>, <c>NotPrepared</c> o <c>Prepared</c>; e
/// <c>Invoiced</c>, la marca de facturado. Sin capa de compatibilidad: no hay compradores en producción.
/// <c>IsStorePickup</c> viaja también en la lista (owner, 2026-11-11): con él la tienda muestra «Listo para recoger» o
/// «Enviado» para un pedido preparado sin abrir el detalle.</summary>
public sealed record BuyerOrderSummary(
    Guid Id,
    string Number,
    DateTimeOffset CreatedAt,
    string Status,
    string PaymentStatus,
    string FulfillmentStatus,
    bool Invoiced,
    decimal Total,
    string Currency,
    int LineCount,
    string FirstLineName,
    bool CanCancel,
    bool IsStorePickup);

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
    string FulfillmentStatus,
    bool Invoiced,
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

    /// <summary>Spec 2026-11-15 §3.2: enlaza al comprador los pedidos de la tienda del tenant sin cuenta, sin decisión de un
    /// asesor y con el correo de facturación <paramref name="normalizedEmail"/>. No sube la versión. Devuelve cuántos
    /// enlazó.</summary>
    Task<int> LinkByEmailAsync(
        Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken cancellationToken);
}

public sealed class BuyerOrderGateway(
    IOrderRepository orders,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IClock clock,
    IQuotationGeographyLookup geography,
    IQuotationProductLookup products)
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
            ? ToDetail(order, await ResolveRefsAsync(order, cancellationToken))
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
            // Nombres y fotos se resuelven antes de anular: si un lookup falla, el pedido queda intacto.
            var refs = await ResolveRefsIfCancellableAsync(order, cancellationToken);
            await CancelAndSaveAsync(tenantId, buyerAccountId, order, cancellationToken);
            return ToDetail(order, refs);
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
            var refs = await ResolveRefsIfCancellableAsync(reread, cancellationToken);
            await CancelAndSaveAsync(tenantId, buyerAccountId, reread, cancellationToken);
            return ToDetail(reread, refs);
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

    public Task<int> LinkByEmailAsync(
        Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken cancellationToken) =>
        orders.LinkStoreOrdersByBillingEmailAsync(tenantId, buyerAccountId, normalizedEmail, cancellationToken);

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
        lifecycleEvents.PublishOrderCancelled(order, fromPlatform: false, now);
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
            order.FulfillmentStatus.ToString(),
            order.IsInvoiced,
            order.Total,
            order.Currency,
            order.Items.Count,
            FirstLineName(order),
            order.CanBeCancelledByBuyer,
            order.IsStorePickup);

    /// <summary>Lo que el detalle trae de otros módulos: nombres de lugares (Geography) y variantes con su foto
    /// (Catalog).</summary>
    private sealed record DetailRefs(
        IReadOnlyDictionary<Guid, string> Departments,
        IReadOnlyDictionary<Guid, string> Cities,
        IReadOnlyDictionary<Guid, QuotationVariantRef> Variants)
    {
        public static readonly DetailRefs None = new(
            new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, QuotationVariantRef>());
    }

    // Sólo se muestra la parte que corresponde; la facturación que no se muestra no se consulta.
    private static (BilledPartySource BilledTo, OrderParty? Billing, OrderParty? Shipping) PartiesShown(Order order)
    {
        var billedTo = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing);
        return (
            billedTo,
            billedTo == BilledPartySource.BillingParty ? order.Billing : null,
            order.IsStorePickup ? null : order.Shipping);
    }

    // Un pedido que ya no se puede anular responde 409 sin tocar Geography ni Catalog: CancelAndSaveAsync lo rechaza.
    private async Task<DetailRefs> ResolveRefsIfCancellableAsync(Order order, CancellationToken cancellationToken) =>
        order.CanBeCancelledByBuyer
            ? await ResolveRefsAsync(order, cancellationToken)
            : DetailRefs.None;

    private async Task<DetailRefs> ResolveRefsAsync(Order order, CancellationToken cancellationToken)
    {
        var (_, billingParty, shippingParty) = PartiesShown(order);

        // Un diccionario por llamada y sólo si hay ids: facturación y envío en la misma ciudad piden una vez. Una
        // excepción del lookup sale tal cual; nunca se disfraza de nombre nulo.
        var colombian = new[] { billingParty, shippingParty }
            .Where(party => party is not null && IsColombian(party))
            .ToArray();
        var departments = await NamesAsync(
            colombian.Select(party => party!.DepartmentId), geography.FindDepartmentNamesAsync, cancellationToken);
        var cities = await NamesAsync(
            colombian.Select(party => party!.CityId), geography.FindCityNamesAsync, cancellationToken);

        // Una sola consulta por pedido, con las variantes distintas; la foto es la actual del catálogo. Igual que la
        // geografía, una excepción sale tal cual y nunca se disfraza de foto nula.
        var variantIds = order.Items.Select(item => item.VariantId).Distinct().ToArray();
        var variants = variantIds.Length == 0
            ? DetailRefs.None.Variants
            : await products.FindManyAsync(order.TenantId, variantIds, cancellationToken);
        return new DetailRefs(departments, cities, variants);
    }

    private static BuyerOrderDetail ToDetail(Order order, DetailRefs refs)
    {
        var (billedTo, billingParty, shippingParty) = PartiesShown(order);
        var departments = refs.Departments;
        var cities = refs.Cities;

        var appliesVatSurplus = order.AppliesVatSurplus;
        return new BuyerOrderDetail(
            order.Id.Value,
            order.OrderNumber,
            order.CreatedAt,
            order.Status.ToString(),
            order.PaymentStatus.ToString(),
            order.FulfillmentStatus.ToString(),
            order.IsInvoiced,
            order.Total,
            order.Currency,
            order.Items.Count,
            FirstLineName(order),
            order.Items
                .OrderBy(item => item.Position)
                .Select(item => ToLine(item, appliesVatSurplus, refs.Variants))
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

    private static string CountryOf(OrderParty party) => party.EffectiveCountry ?? QuotationPartyDetails.ColombiaCountryCode;

    // Colombia es el país efectivo nulo (envío, o facturación vieja o pendiente) o CO literal (la facturación desde el spec 2026-11-27):
    // sólo ahí los nombres salen de Geography por id.
    private static bool IsColombian(OrderParty party) =>
        party.EffectiveCountry is null or QuotationPartyDetails.ColombiaCountryCode;

    private static string? DepartmentName(OrderParty party, IReadOnlyDictionary<Guid, string> departments) =>
        IsColombian(party) && party.DepartmentId is { } id && departments.TryGetValue(id, out var name) ? name : null;

    private static string? CityName(OrderParty party, IReadOnlyDictionary<Guid, string> cities) =>
        !IsColombian(party)
            ? party.CityName
            : party.CityId is { } id && cities.TryGetValue(id, out var name) ? name : null;

    private static BuyerOrderLine ToLine(
        OrderItem item, bool appliesVatSurplus, IReadOnlyDictionary<Guid, QuotationVariantRef> variants)
    {
        var imageUrl = variants.GetValueOrDefault(item.VariantId)?.ImageUrl;
        return appliesVatSurplus
            ? new BuyerOrderLine(
                item.ProductName ?? string.Empty, item.Quantity, item.UnitPriceWithoutTax, item.Subtotal, imageUrl)
            : new BuyerOrderLine(
                item.ProductName ?? string.Empty, item.Quantity, item.UnitPrice, item.Subtotal + item.TaxAmount, imageUrl);
    }

    private static string FirstLineName(Order order) =>
        order.Items.OrderBy(item => item.Position).FirstOrDefault()?.ProductName ?? string.Empty;
}
