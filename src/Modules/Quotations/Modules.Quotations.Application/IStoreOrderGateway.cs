using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

public sealed record StoreQuoteLine(Guid VariantId, decimal Quantity);

public sealed record StoreQuoteRequest(Guid TenantId, string Currency, bool IsRetail, IReadOnlyList<StoreQuoteLine> Lines);

/// <summary>Una línea del carrito ya preciada por el motor de pedidos. <c>LineTotal</c> es
/// <c>Subtotal + TaxAmount</c> de la línea: el precio de lista lleva el IVA adentro, así que es lo que la línea
/// suma al <c>Total</c>.</summary>
public sealed record StoreQuotedLine(
    Guid VariantId,
    Guid ProductId,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    string DiscountOrigin,
    decimal DiscountedUnitPrice,
    decimal LineTotal);

public sealed record StoreQuote(
    string Currency,
    IReadOnlyList<StoreQuotedLine> Lines,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Total);

/// <summary>Una parte del pedido de la tienda. La identificación va sólo en facturación; <c>Email</c> es el de
/// facturación en las dos. Ciudad por carril: Colombia con <c>DepartmentId</c> + <c>CityId</c>, otro país con
/// <c>CityName</c>.</summary>
public sealed record StoreOrderParty(
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

/// <summary>Lo que el checkout de la tienda entrega al motor de pedidos. <c>Shipping</c> nunca es null: la tienda
/// pasa los datos de facturación cuando el visitante marcó «Usar los mismos datos». <c>ClientRequestId</c> es el id
/// que el navegador genera por checkout: de él sale la clave de idempotencia. No hay persona detrás: el pedido lo
/// crea el sistema.</summary>
public sealed record StoreOrderDraft(
    Guid TenantId,
    Guid ClientRequestId,
    Guid ClientId,
    Guid AdvisorMembershipId,
    bool IsRetail,
    QuotationBillingAccountRequest BillingAccount,
    StoreOrderParty Billing,
    StoreOrderParty Shipping,
    bool BillingWithRetention,
    bool BillingVatSurplus,
    IReadOnlyList<StoreQuoteLine> Lines,
    string? Notes);

/// <summary>El pedido de la tienda. Con <c>AlreadyExisted</c> el resto describe el pedido que ya estaba (cliente y
/// asesor incluidos), no lo que trajo la llamada repetida.</summary>
public sealed record StoreOrderResult(
    Guid OrderId,
    string OrderNumber,
    decimal Total,
    string Currency,
    Guid ClientId,
    Guid AdvisorMembershipId,
    bool AlreadyExisted);

/// <summary>El puerto de la tienda pública hacia el motor de pedidos: cotiza el carrito y crea el pedido del
/// checkout. Los totales salen siempre de acá; el navegador nunca los calcula.</summary>
public interface IStoreOrderGateway
{
    /// <summary>Precia el carrito con las reglas de un pedido. No guarda nada.</summary>
    Task<StoreQuote> QuoteAsync(StoreQuoteRequest request, CancellationToken cancellationToken);

    /// <summary>Crea el pedido del checkout; idempotente por <see cref="StoreOrderGateway.ExternalKeyFor"/>.</summary>
    Task<StoreOrderResult> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken);

    /// <summary>El pedido que ya salió de ese checkout, o <c>null</c>. Se mira antes de tocar al cliente.</summary>
    Task<StoreOrderResult?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);

    /// <summary>Cuántos pedidos de la tienda entraron desde <paramref name="since"/>, anulados incluidos: el tope
    /// diario no devuelve cupo al anular.</summary>
    Task<int> CountStoreOrdersSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class StoreOrderGateway(
    OrderDraftBuilder drafts,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
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
    ITenantDirectory tenantDirectory,
    ITenantClock tenantClock)
    : IStoreOrderGateway
{
    /// <summary>La clave de idempotencia del pedido de un checkout: el reintento del navegador devuelve el mismo pedido.</summary>
    public static string ExternalKeyFor(Guid clientRequestId) => $"store-checkout:{clientRequestId}";

    public async Task<StoreQuote> QuoteAsync(StoreQuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = Currencies.Normalize(request.Currency);
        var lines = await drafts.PriceLinesAsync(
            request.TenantId, MergeRepeated(request.Lines), currency, request.IsRetail, cancellationToken);
        var now = (await tenantClock.GetAsync(request.TenantId, cancellationToken)).UtcNow;

        // Un cálculo en memoria: el pedido tiene número de relleno y cliente vacío, así que nada de esto toca el
        // repositorio ni el unit of work.
        var order = Order.QuoteForStore(request.TenantId, currency, request.IsRetail, lines, now);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, request.TenantId, order, now, cancellationToken);

        var missingNames = order.Items.Where(item => item.ProductName is null).Select(item => item.VariantId).ToArray();
        var fallbackNames = missingNames.Length == 0
            ? new Dictionary<Guid, QuotationVariantRef>()
            : (await productLookup.FindManyAsync(request.TenantId, missingNames, cancellationToken))
                .ToDictionary(entry => entry.Key, entry => entry.Value);

        var quoted = order.Items
            .Select(item => new StoreQuotedLine(
                item.VariantId,
                item.ProductId,
                item.ProductName ?? fallbackNames.GetValueOrDefault(item.VariantId)?.Name ?? string.Empty,
                item.Quantity,
                item.UnitPrice,
                item.DiscountPercentage,
                item.DiscountOrigin.ToString(),
                item.DiscountedUnitPriceUnder(order.PriceRounding),
                item.Subtotal + item.TaxAmount))
            .ToArray();
        return new StoreQuote(
            order.Currency, quoted, order.Subtotal, order.DiscountAmount, order.TaxAmount, order.Total);
    }

    public async Task<StoreOrderResult> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // Con clave, un pedido que ya entró se devuelve tal cual, sin escribir nada: si el cliente quedó inactivo
        // después, el reintento igual encuentra su pedido.
        var externalKey = ExternalKeyFor(draft.ClientRequestId);
        if (await orderRepository.FindByExternalKeyAsync(
                draft.TenantId, OrderSource.Store, externalKey, cancellationToken) is { } existing)
        {
            return Existing(existing);
        }

        var customer = await customerLookup.FindAsync(draft.TenantId, draft.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, draft.TenantId, draft.ClientId, allowIncomplete: true);
        // La cuenta es la entrada de la tienda para la moneda de la solicitud: de ella sale la moneda del pedido
        // y con ella se precian las líneas, nunca con una moneda aparte que pudiera discrepar.
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, draft.TenantId, draft.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing account.");

        var lines = await drafts.PriceLinesAsync(
            draft.TenantId, MergeRepeated(draft.Lines), billingAccount.Currency, draft.IsRetail, cancellationToken);
        var advisor = new MemberId(draft.AdvisorMembershipId);
        var header = new OrderHeader(
            draft.ClientId, advisor, billingAccount.Currency, null, draft.IsRetail, GlobalScaleFloor: null,
            customer.WithRetention, customer.VatSurplus);
        var parties = new QuotationPartiesRequest(
                Billing: ToParty(draft.Billing),
                Shipping: ToParty(draft.Shipping),
                BillingUsesBusinessName: false,
                IsStorePickup: false,
                BillsToFinalConsumer: false,
                BillingWithRetention: draft.BillingWithRetention,
                BillingVatSurplus: draft.BillingVatSurplus)
            .ToDomain();
        var regional = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(draft.TenantId), cancellationToken);
        BillingPartyFiscalRules.EnsureConsistent(parties.Billing, regional?.Country);

        var committed = await KeyedOrderCommit.CommitAsync(
            orderRepository, unitOfWork, numberGenerator, numberingFormats, tenantClock, draft.TenantId, externalKey,
            async (orderNumber, now) =>
            {
                var order = Order.CreateFromStore(
                    OrderId.New(), draft.TenantId, orderNumber, header, parties, billingAccount, lines, draft.Notes,
                    advisor, now, externalKey);
                await OrderPricingRecalculation.ApplyAsync(
                    pricingLookup, settingsStore, priceRounding, draft.TenantId, order, now, cancellationToken);
                return order;
            },
            (order, now) =>
            {
                // Ninguna persona crea este pedido: el historial va sin autor y la auditoría, como integración.
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, memberId: null,
                    OrderChangeSummary.CreatedFromStore(), now));
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
        var winner = await orderRepository.FindByExternalKeyAsync(
                draft.TenantId, OrderSource.Store, externalKey, cancellationToken)
            ?? throw new InvalidOperationException(
                "The external key was taken, but no committed order carries it.");
        return Existing(winner);
    }

    public async Task<StoreOrderResult?> FindExistingAsync(
        Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
        await orderRepository.FindByExternalKeyAsync(
                tenantId, OrderSource.Store, ExternalKeyFor(clientRequestId), cancellationToken)
            is { } order
            ? Existing(order)
            : null;

    public Task<int> CountStoreOrdersSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
        orderRepository.CountBySourceSinceAsync(tenantId, OrderSource.Store, since, cancellationToken);

    private static StoreOrderResult Existing(Order order) => ToResult(order, alreadyExisted: true);

    private static StoreOrderResult ToResult(Order order, bool alreadyExisted) =>
        new(order.Id.Value, order.OrderNumber, order.Total, order.Currency, order.ClientId, order.AdvisorId.Value, alreadyExisted);

    // El motor rechaza una variante repetida (CreateOrder); el carrito de la tienda las junta antes.
    private static List<OrderItemAddition> MergeRepeated(IReadOnlyList<StoreQuoteLine> lines) =>
        lines
            .GroupBy(line => line.VariantId)
            .Select(group => new OrderItemAddition(group.Key, group.Sum(line => line.Quantity)))
            .ToList();

    private static QuotationPartyRequest ToParty(StoreOrderParty party) =>
        new(
            party.Name,
            party.IdentificationNumber,
            party.Phone,
            party.Email,
            party.Address,
            party.DepartmentId,
            party.CityId,
            IdentificationType: party.IdentificationType,
            Country: party.Country,
            CityName: party.CityName);
}
