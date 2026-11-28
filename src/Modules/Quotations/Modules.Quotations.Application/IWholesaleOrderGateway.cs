using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea de la orden de compra con el precio de lista del distribuidor ya calculado (IVA incluido, D-5).</summary>
public sealed record WholesaleOrderLine(Guid PurchaseOrderLineId, Guid VariantId, decimal Quantity, decimal UnitPrice);

/// <summary>
/// Lo que la orden de compra vinculada entrega al motor de pedidos del mayorista. Cliente, asesor, empresa facturadora y moneda
/// salen de la relación (Wholesale) y nunca del cuerpo del distribuidor.
/// </summary>
public sealed record WholesaleOrderDraft(
    Guid TenantId,
    Guid BuyerTenantId,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    Guid ClientId,
    Guid AdvisorMembershipId,
    Guid BillingCompanyId,
    string Currency,
    string? Notes,
    IReadOnlyList<WholesaleOrderLine> Lines);

/// <summary>
/// Una línea del pedido tal como quedó guardada. <c>PurchaseOrderLineId</c> es nulo sólo si el mayorista la agregó antes de que
/// el reintento del envío la leyera (D-8): la orden la adopta como línea nueva. La tasa es decimal en todo el contrato
/// (ruling X5).
/// </summary>
public sealed record WholesaleOrderResultLine(
    Guid? PurchaseOrderLineId,
    Guid VariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxPercentage);

/// <summary>El pedido del mayorista. Con <c>AlreadyExisted</c> el resto describe el pedido que ya estaba (D-8).</summary>
public sealed record WholesaleOrderResult(
    Guid OrderId,
    string OrderNumber,
    string Currency,
    IReadOnlyList<WholesaleOrderResultLine> Lines,
    bool AlreadyExisted);

/// <summary>
/// El puerto de Compras (vía Bootstrapper) hacia el motor de pedidos del mayorista (mayorista fase 3). Todo método recibe el
/// tenant mayorista y el comprador, y comprueba que el pedido sea de <b>ese</b> comprador (ruling X1): el id de la orden de
/// otro comprador no encuentra ni anula nada.
/// </summary>
public interface IWholesaleOrderGateway
{
    /// <summary>El pedido que ya salió de esa orden de compra de ese comprador, o <c>null</c>.</summary>
    Task<WholesaleOrderResult?> FindAsync(
        Guid tenantId, Guid buyerTenantId, Guid purchaseOrderId, CancellationToken cancellationToken);

    /// <summary>Crea el pedido con los precios dados; idempotente por <see cref="Order.WholesaleExternalKey"/>.</summary>
    Task<WholesaleOrderResult> CreateAsync(WholesaleOrderDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// D-12: el distribuidor anula su orden y, con ella, el pedido mientras nadie lo apruebe. <c>false</c> si ya estaba anulado
    /// (un reintento); otro comprador, un pedido aprobado o tocado es <c>order.order.not_cancellable_by_buyer</c>.
    /// </summary>
    Task<bool> CancelByBuyerAsync(
        Guid tenantId,
        Guid orderId,
        Guid buyerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber,
        CancellationToken cancellationToken);
}

/// <summary>Una línea final del evento <c>quotations.order.wholesale-approved.v1</c> (contrato de eventos de la fase 3).</summary>
public sealed record WholesaleApprovedLineData(
    Guid? SourcePurchaseOrderLineId,
    Guid VariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxPercentage,
    Guid LineId,
    string ProductKind);

/// <summary>Las líneas del evento de aprobación, armadas en un solo lugar para el publicador del outbox y las pruebas.</summary>
public static class WholesaleOrderEvents
{
    /// <summary>
    /// Todas las líneas finales, en su orden. <c>UnitPrice</c> es IVA incluido y <c>TaxPercentage</c> la tasa efectiva de la
    /// línea. <c>Code</c> y <c>Name</c> salen de la foto de la línea, que el camino mayorista siempre captura; una línea sin
    /// foto viaja con texto vacío y la orden de compra la muestra con lo que tenga.
    /// </summary>
    public static IReadOnlyList<WholesaleApprovedLineData> ApprovedLines(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.Items
            .OrderBy(item => item.Position)
            .Select(item => new WholesaleApprovedLineData(
                item.SourcePurchaseOrderLineId,
                item.VariantId,
                item.ProductCode ?? string.Empty,
                item.ProductName ?? string.Empty,
                item.Quantity,
                item.UnitPrice,
                item.TaxPercentage,
                item.Id.Value,
                item.Kind.ToString()))
            .ToArray();
    }
}

/// <summary>
/// Calca <see cref="StoreOrderGateway"/>: el pedido se precia con el precio de lista dado (D-5: sin escalas ni descuentos), con la
/// tasa y la foto del catálogo; sin detal; la empresa facturadora de la relación con su cuenta en la moneda del pedido (sin
/// cuenta en esa moneda, la empresa sola, spec 2026-11-17 R3); cliente y asesor dados. Ninguna persona del mayorista lo crea:
/// historial sin autor y auditoría como integración.
/// </summary>
public sealed class WholesaleOrderGateway(
    OrderDraftBuilder drafts,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    ITenantPriceRounding priceRounding,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    ITenantClock tenantClock,
    IQuotationWithholdingLookup withholdingLookup)
    : IWholesaleOrderGateway
{
    public async Task<WholesaleOrderResult?> FindAsync(
        Guid tenantId, Guid buyerTenantId, Guid purchaseOrderId, CancellationToken cancellationToken) =>
        await FindOwnAsync(tenantId, buyerTenantId, purchaseOrderId, cancellationToken) is { } order
            ? ToResult(order, alreadyExisted: true)
            : null;

    public async Task<WholesaleOrderResult> CreateAsync(WholesaleOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Lines.Count == 0)
        {
            throw new ArgumentException("A wholesale order needs at least one line.", nameof(draft));
        }

        // D-8: un reintento devuelve el pedido tal como está guardado, sin revalidar nada.
        if (await FindOwnAsync(draft.TenantId, draft.BuyerTenantId, draft.PurchaseOrderId, cancellationToken) is { } existing)
        {
            return ToResult(existing, alreadyExisted: true);
        }

        var currency = Currencies.Normalize(draft.Currency);
        var customer = await customerLookup.FindAsync(draft.TenantId, draft.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, draft.TenantId, draft.ClientId, allowIncomplete: true);
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup,
                draft.TenantId,
                await AccountRequestAsync(draft.TenantId, draft.BillingCompanyId, currency, cancellationToken),
                currency,
                cancellationToken)
            ?? throw new QuotationsDomainException("order.billing.account_required", "An order needs a billing company.");

        // La tasa, el producto y la foto salen del catálogo; el precio es el dado y el descuento 0 (D-5).
        var priced = await drafts.PriceLinesAsync(
            draft.TenantId,
            draft.Lines.Select(line => new OrderItemAddition(line.VariantId, line.Quantity)).ToArray(),
            billingAccount.Currency,
            isRetail: false,
            cancellationToken);
        var lines = priced
            .Zip(draft.Lines, (line, source) => line with
            {
                UnitPrice = source.UnitPrice,
                DiscountPercentage = 0m,
                DiscountOrigin = QuotationDiscountOrigin.Own,
                SourcePurchaseOrderLineId = source.PurchaseOrderLineId,
            })
            .ToList();

        var advisor = new MemberId(draft.AdvisorMembershipId);
        var calendar = await tenantClock.GetAsync(draft.TenantId, cancellationToken);
        var withholdings = await OrderCustomerWithholdings.ResolveAsync(
            withholdingLookup, draft.TenantId, draft.ClientId, billingAccount,
            DocumentDates.LocalDate(calendar, calendar.UtcNow), cancellationToken);
        var header = new OrderHeader(
            draft.ClientId, advisor, billingAccount.Currency, null, IsRetail: false, GlobalScaleFloor: null,
            customer.VatSurplus, withholdings);
        // Las partes del cliente: el distribuidor no manda direcciones propias en esta fase.
        var parties = ((QuotationPartiesRequest?)null).ToDomain();
        var externalKey = Order.WholesaleExternalKey(draft.BuyerTenantId, draft.PurchaseOrderId);

        var committed = await KeyedOrderCommit.CommitAsync(
            orderRepository, unitOfWork, numberGenerator, numberingFormats, tenantClock, draft.TenantId, externalKey,
            async (orderNumber, now) =>
            {
                var order = Order.CreateFromWholesale(
                    OrderId.New(), draft.TenantId, orderNumber, header, parties, billingAccount, lines, draft.Notes, advisor,
                    now, DocumentDates.LocalDate(calendar, now), draft.BuyerTenantId, draft.PurchaseOrderId,
                    draft.PurchaseOrderNumber);
                await OrderPricingRecalculation.ApplyAsync(
                    pricingLookup, settingsStore, priceRounding, draft.TenantId, order, now, cancellationToken);
                return order;
            },
            (order, now) =>
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, memberId: null,
                    OrderChangeSummary.CreatedFromPurchaseOrder(draft.PurchaseOrderNumber), now));
                auditPublisher.PublishByIntegration(
                    draft.TenantId, "quotation.order.created", order.Id.ToString(), "success", now);
                lifecycleEvents.PublishOrderCreated(order, now);
            },
            cancellationToken);
        if (committed is not null)
        {
            return ToResult(committed, alreadyExisted: false);
        }

        // Perdió la carrera de la clave: el ganador ya está confirmado.
        var winner = await FindOwnAsync(draft.TenantId, draft.BuyerTenantId, draft.PurchaseOrderId, cancellationToken)
            ?? throw new InvalidOperationException("The external key was taken, but no committed order carries it.");
        return ToResult(winner, alreadyExisted: true);
    }

    public async Task<bool> CancelByBuyerAsync(
        Guid tenantId,
        Guid orderId,
        Guid buyerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber,
        CancellationToken cancellationToken)
    {
        // Un pedido que no está (otro tenant, otro id) responde lo mismo que uno ajeno: el comprador no aprende nada.
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw new QuotationsDomainException(
                BuyerOrderErrors.NotCancellableByBuyer, "The order cannot be cancelled by this buyer.");
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        if (!order.CancelByWholesaleBuyer(
                buyerTenantId, purchaseOrderId, $"Cancelada por el distribuidor ({purchaseOrderNumber}).", now))
        {
            return false;
        }

        // D-22: la traza queda en el mayorista, con actor vacío porque no hay persona suya detrás.
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, memberId: null,
            OrderChangeSummary.Cancelled(order.CancellationReason!), now));
        auditPublisher.Publish(tenantId, Guid.Empty, "quotation.order.cancelled", order.Id.ToString(), "success", now);
        lifecycleEvents.PublishOrderCancelled(order, fromPlatform: false, now);
        lifecycleEvents.PublishWholesaleCancelled(order, byBuyer: true, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Ruling X1: la clave ya lleva al comprador; el pedido encontrado se vuelve a comprobar contra él, por si acaso.
    private async Task<Order?> FindOwnAsync(
        Guid tenantId, Guid buyerTenantId, Guid purchaseOrderId, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByExternalKeyAsync(
            tenantId, OrderSource.Wholesale, Order.WholesaleExternalKey(buyerTenantId, purchaseOrderId), cancellationToken);
        return order is not null
            && order.TenantId == tenantId
            && order.BuyerTenantId == buyerTenantId
            && order.PurchaseOrderId == purchaseOrderId
                ? order
                : null;
    }

    // La cuenta de la empresa en la moneda del pedido; sin ninguna, la empresa sola (R3). Una empresa ajena o borrada la
    // rechaza el resolver con su código.
    private async Task<QuotationBillingAccountRequest> AccountRequestAsync(
        Guid tenantId, Guid companyId, string currency, CancellationToken cancellationToken)
    {
        var company = await companyLookup.FindAsync(tenantId, companyId, cancellationToken);
        var account = company?.BankAccounts.FirstOrDefault(candidate =>
            string.Equals(candidate.Currency, currency, StringComparison.OrdinalIgnoreCase));
        return account is null
            ? new QuotationBillingAccountRequest(companyId, null, null, null)
            : new QuotationBillingAccountRequest(companyId, account.BankName, account.AccountNumber, account.Currency);
    }

    private static WholesaleOrderResult ToResult(Order order, bool alreadyExisted) =>
        new(
            order.Id.Value,
            order.OrderNumber,
            order.Currency,
            order.Items
                .OrderBy(item => item.Position)
                .Select(item => new WholesaleOrderResultLine(
                    item.SourcePurchaseOrderLineId,
                    item.VariantId,
                    item.ProductCode ?? string.Empty,
                    item.ProductName ?? string.Empty,
                    item.Quantity,
                    item.UnitPrice,
                    item.TaxPercentage))
                .ToArray(),
            alreadyExisted);
}
