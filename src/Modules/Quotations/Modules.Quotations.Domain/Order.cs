using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido con datos propios (spec 2026-10-15): cliente, asesor, cuenta de cobro, partes, líneas y
/// totales. Nace de una cotización (<see cref="FromQuotation"/>, copia independiente), a mano
/// (<see cref="CreateManual"/>) o desde una integración (<see cref="CreateExternal"/>). Sólo mientras
/// es <see cref="IsEditable"/> (abierto, sin preparar y sin facturar; spec 2026-11-10 §3.6) se editan sus líneas,
/// partes y comprobantes. Agregado raíz que incluye sus comprobantes de pago (<see cref="OrderPaymentProof"/>):
/// nacen con él, pero mientras se edita se le pueden sumar más y corregir el monto de los que ya tiene.
///
/// Tiene cuatro ejes independientes (spec 2026-11-10): <see cref="Status"/>, <see cref="PaymentStatus"/>,
/// <see cref="FulfillmentStatus"/> y la marca de facturado (<see cref="IsInvoiced"/>). Todas las transiciones viven acá.
///
/// <see cref="Preview"/> arma el cálculo previo del pedido manual, que no se guarda y es el único que puede
/// no tener cuenta de cobro.
///
/// <see cref="ConvertedAt"/> y <see cref="ConvertedBy"/> conservan su nombre para un pedido manual o
/// externo: significan «creado el / por» (plan 2026-10-15, P3).
///
/// Nunca tiene PDF ni se le manda al cliente (US-17): el único documento que el cliente recibe es la
/// cotización original. La entrega por envío (transportadora, valor, contraentrega) sí la tiene
/// (spec 2026-10-17 flete): ver <see cref="ShippingDetails"/>.
/// </summary>
public sealed class Order
{
    public const int OrderNumberMaxLength = 20;

    /// <summary>Mismo límite que las notas tenían (spec 2026-09-16, decisión 4).</summary>
    public const int CancellationReasonMaxLength = 500;

    public const int ExternalReferenceMaxLength = 64;

    /// <summary>La clave de idempotencia del pedido externo (D-S23): id de la tienda más id del pedido.</summary>
    public const int ExternalKeyMaxLength = 128;

    /// <summary>El nombre del método de envío en la tienda; informativo, así que se trunca sin error.</summary>
    public const int ExternalShippingTitleMaxLength = 120;

    public const int PaymentMethodMaxLength = Quotation.PaymentMethodMaxLength;

    private readonly List<OrderPaymentProof> _paymentProofs = [];
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderParty> _parties = [];

    // La regla con que se preció el documento (spec 2026-10-27 §4). Nulas en un documento anterior a la
    // regla: se leen con la de siempre de su moneda (D4). EF las materializa por el campo.
    private int? _priceRoundingDecimals;
    private PriceRoundingMode? _priceRoundingMode;

    private Order()
    {
        OrderNumber = string.Empty;
        Currency = string.Empty;
    }

    private Order(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderSource source,
        QuotationId? quotationId,
        string? externalReference,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        OrderPaymentStatus? paymentStatus,
        string? notes,
        MemberId createdBy,
        DateTimeOffset occurredAt,
        bool allowNoClient = false)
    {
        Id = id;
        TenantId = tenantId;
        OrderNumber = NormalizeOrderNumber(orderNumber);
        Source = source;
        QuotationId = quotationId;
        ExternalReference = NormalizeExternalReference(externalReference);
        // Sólo QuoteForStore llega sin cliente: es un cálculo que no se guarda (spec de la tienda §5.2).
        ClientId = header.ClientId == Guid.Empty && !allowNoClient
            ? throw new QuotationsDomainException("order.order.client_required", "The order client is required.")
            : header.ClientId;
        AdvisorId = header.AdvisorId;
        PaymentMethod = NormalizePaymentMethod(header.PaymentMethod);
        IsRetail = header.IsRetail;
        EnsureGlobalScaleFloorAllowed(header.GlobalScaleFloor, header.IsRetail);
        GlobalScaleFloor = header.GlobalScaleFloor;
        CustomerWithRetention = header.CustomerWithRetention;
        CustomerVatSurplus = header.CustomerVatSurplus;
        EnsureBillingIsConsistent(parties.BillsToFinalConsumer, parties.Billing is not null, parties.BillingUsesBusinessName);
        AssignParties(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        IsStorePickup = parties.IsStorePickup;
        BillsToFinalConsumer = parties.BillsToFinalConsumer;
        PartyWithRetention = parties.Billing is null ? null : parties.BillingWithRetention;
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        BillingAccount = billingAccount?.Normalized();
        // La misma regla que Quotation: la cuenta fija la moneda y, sin ella, manda la del encabezado, que
        // Application llena con la moneda por defecto del tenant. Sólo el cálculo previo llega acá sin cuenta.
        Currency = BillingAccount?.Currency ?? header.Currency;
        Status = OrderStatus.Open;
        FulfillmentStatus = OrderFulfillmentStatus.NotPrepared;
        // Null = lo calculan los comprobantes, que se agregan después (ver CalculatedPaymentStatus).
        PaymentStatus = paymentStatus ?? OrderPaymentStatus.PaymentPending;
        Notes = NormalizeNotes(notes);
        ConvertedAt = occurredAt;
        ConvertedBy = createdBy;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version = 1;
    }

    public OrderId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant. Formato <c>PED-2026-0001</c>, emitido por
    /// <c>IOrderNumberGenerator</c> — mismo mecanismo que <c>QuotationNumber</c>.</summary>
    public string OrderNumber { get; private set; }

    public OrderSource Source { get; private set; }

    /// <summary>La cotización de la que nació, si nació de una. Única a nivel de base entre las no nulas:
    /// una cotización tiene a lo sumo un pedido.</summary>
    public QuotationId? QuotationId { get; private set; }

    /// <summary>El número del pedido en el sistema de origen (<c>#1001</c> en Shopify). Null en los
    /// pedidos de cotización y manuales.</summary>
    public string? ExternalReference { get; private set; }

    /// <summary>Spec de Shopify D-S23: la clave que hace idempotente el camino externo (por ejemplo, id de la
    /// tienda más id del pedido). Única por <c>(tenant, source)</c> entre las no nulas; null en los pedidos de
    /// cotización y manuales.</summary>
    public string? ExternalKey { get; private set; }

    /// <summary>La cuenta de comprador de la tienda que hizo el pedido; null en los pedidos que no vienen de una cuenta.</summary>
    public Guid? BuyerAccountId { get; private set; }

    /// <summary>El comprador sólo anula lo que nadie ha tocado (spec 2026-11-10 §3.7): abierto, con el pago en
    /// PaymentPending, sin ningún comprobante registrado (un asesor puede dejar un comprobante con el pago aún en
    /// PaymentPending), sin preparar y sin facturar.</summary>
    public bool CanBeCancelledByBuyer =>
        Status == OrderStatus.Open
        && PaymentStatus == OrderPaymentStatus.PaymentPending
        && PaymentProofs.Count == 0
        && !IsPrepared
        && !IsInvoiced;

    /// <summary>Cómo llamó la tienda al envío (<c>Envío estándar</c>). Sólo se muestra: no decide nada.</summary>
    public string? ExternalShippingTitle { get; private set; }

    public Guid ClientId { get; private set; }

    public MemberId AdvisorId { get; private set; }

    public string Currency { get; private set; }

    /// <summary>La regla de redondeo con la que se preció este documento. Cambiar la regla del tenant
    /// después no la mueve: sólo la cambia <see cref="UsePriceRounding"/>, que corre dentro del
    /// recálculo de una edición. Con cualquiera de las dos columnas nula (un documento anterior a la
    /// regla) es la de siempre de su moneda.</summary>
    public PriceRounding PriceRounding =>
        _priceRoundingDecimals is { } decimals && _priceRoundingMode is { } mode
            ? new PriceRounding(decimals, mode)
            : PriceRounding.LegacyFor(Currency);

    /// <summary>
    /// Guarda la regla de redondeo y rehace <b>todas</b> las líneas con su cantidad, precio, descuento,
    /// origen y tasa actuales. Si es igual a la vigente no hace nada.
    ///
    /// **No es una edición.** No toca <see cref="Version"/> ni <c>Updated*</c>, mismo contrato que
    /// <c>ApplyGroupDiscounts</c>, y no exige un estado editable: la llama sólo el recálculo, que ya
    /// corre dentro de una edición.
    /// </summary>
    public void UsePriceRounding(PriceRounding rounding, DateTimeOffset occurredAt)
    {
        if (rounding == PriceRounding)
        {
            return;
        }

        _priceRoundingDecimals = rounding.Decimals;
        _priceRoundingMode = rounding.Mode;
        foreach (var item in _items)
        {
            item.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, Currency, rounding, occurredAt);
        }

        RecalculateTotals();
    }

    /// <summary>Null sólo en un cálculo previo (<see cref="Preview"/>): la pantalla de «Nuevo pedido» precia
    /// la línea antes de que se elija la empresa que factura. Un pedido guardado siempre la tiene:
    /// <see cref="FromQuotation"/>, <see cref="CreateManual"/> y <see cref="CreateExternal"/> la exigen.</summary>
    public QuotationBillingAccount? BillingAccount { get; private set; }

    public string? PaymentMethod { get; private set; }

    public bool IsRetail { get; private set; }

    public int? GlobalScaleFloor { get; private set; }

    public bool BillingUsesBusinessName { get; private set; }

    public bool IsStorePickup { get; private set; }

    public bool BillsToFinalConsumer { get; private set; }

    public bool? PartyWithRetention { get; private set; }

    public bool? PartyVatSurplus { get; private set; }

    public bool CustomerWithRetention { get; private set; }

    public bool CustomerVatSurplus { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal TaxPercentage { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal Total { get; private set; }

    public decimal RetentionAmount { get; private set; }

    public decimal NetTotal { get; private set; }

    /// <summary>Subtotal del envío, sin IVA. Cero salvo que el envío se cobre en el documento.</summary>
    public decimal FreightSubtotal { get; private set; }

    /// <summary>IVA del envío. Cero con excedente de IVA o fuera de COP.</summary>
    public decimal FreightTaxAmount { get; private set; }

    /// <summary>Lo facturado por los productos, sin el envío.</summary>
    public decimal ProductsTotal => Subtotal + TaxAmount;

    /// <summary>Misma regla que <see cref="Quotation.CarrierId"/>.</summary>
    public Guid? CarrierId { get; private set; }

    public string? CarrierName { get; private set; }

    public FreightChargeMode? FreightChargeMode { get; private set; }

    public decimal FreightAmount { get; private set; }

    public int FreightTaxPercentage { get; private set; }

    /// <summary>El pedido se cobra contraentrega (spec 2026-10-17 flete §6): nace
    /// <see cref="OrderPaymentStatus.PaymentPending"/> y el giro se registra con un comprobante.</summary>
    public bool CashOnDelivery { get; private set; }

    /// <summary>Los seis campos del envío juntos, como <see cref="Quotation.ShippingDetails"/>.</summary>
    public ShippingDetails ShippingDetails =>
        new(CarrierId, CarrierName, FreightChargeMode, FreightAmount, FreightTaxPercentage, CashOnDelivery);

    public OrderStatus Status { get; private set; }

    public OrderPaymentStatus PaymentStatus { get; private set; }

    public OrderFulfillmentStatus FulfillmentStatus { get; private set; }

    public bool IsInvoiced => InvoicedAt is not null;

    public bool IsPrepared => FulfillmentStatus == OrderFulfillmentStatus.Prepared;

    /// <summary>La única guarda de edición (spec 2026-11-10 §3.6): abierto, sin preparar y sin facturar. Pagado no
    /// congela, como en Shopify.</summary>
    public bool IsEditable => Status == OrderStatus.Open && !IsPrepared && !IsInvoiced;

    /// <summary>Sin tope (plan 2026-10-15, P2): hereda las notas de la cotización, que no lo tienen.</summary>
    public string? Notes { get; private set; }

    public DateTimeOffset ConvertedAt { get; private set; }

    public MemberId ConvertedBy { get; private set; }

    /// <summary>Cuándo se marcó como pagado, y quién (spec 2026-11-10 §3.2). Null si nadie lo marcó: un pago de la
    /// plataforma no lo llena.</summary>
    public DateTimeOffset? PaidMarkedAt { get; private set; }

    public MemberId? PaidMarkedBy { get; private set; }

    /// <summary>Cuándo se marcó como preparado, y quién (spec 2026-11-10 §3.3).</summary>
    public DateTimeOffset? PreparedAt { get; private set; }

    public MemberId? PreparedBy { get; private set; }

    /// <summary>Cuándo se archivó, y quién. Null en quién cuando lo archivó el sistema sin una persona detrás.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public MemberId? ArchivedBy { get; private set; }

    /// <summary>Cuándo se anuló, quién y por qué (spec 2026-09-16). Van aparte de las otras marcas, que
    /// se conservan: un pedido pagado y después anulado sigue diciendo quién lo marcó pagado.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public MemberId? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Cuándo se marcó como facturado y quién (spec 2026-10-05). QEP no emite la factura;
    /// esto sólo deja constancia de que se hizo afuera.</summary>
    public DateTimeOffset? InvoicedAt { get; private set; }

    public MemberId? InvoicedBy { get; private set; }

    /// <summary>Vacío hasta que se sincronice — placeholder para la integración futura con
    /// Ritual Collection. Esta fase no la implementa.</summary>
    public string? RitualCollectionSyncId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyCollection<OrderPaymentProof> PaymentProofs => _paymentProofs;

    public IReadOnlyCollection<OrderItem> Items => _items;

    public IReadOnlyCollection<OrderParty> Parties => _parties;

    public OrderParty? Billing => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Billing);

    public OrderParty? Shipping => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping);

    /// <summary>Misma regla que <see cref="Quotation.AppliesRetention"/>.</summary>
    public bool AppliesRetention =>
        (Billing is not null ? PartyWithRetention ?? false : CustomerWithRetention)
        && !BillsToFinalConsumer
        && QuotationItem.ItemizesTaxIn(Currency);

    /// <summary>Misma regla que <see cref="Quotation.AppliesVatSurplus"/>.</summary>
    public bool AppliesVatSurplus =>
        (Billing is not null ? PartyVatSurplus ?? false : CustomerVatSurplus) && !BillsToFinalConsumer;

    /// <summary>Copia independiente de la cotización (spec §4.4): la cotización ya pasó por
    /// <c>EnsureConvertibleToOrder</c> y <c>ConvertToOrder</c>; acá no se vuelve a validar su estado.
    /// Las notas del pedido son un solo campo, nunca una concatenación: <paramref name="notes"/> nulo hereda
    /// las de la cotización; con texto, lo reemplaza; vacío o en blanco deja el pedido sin notas (el usuario
    /// borró el texto precargado). El pago nace calculado de los comprobantes (spec 2026-11-10 §3.2): nunca pagado.</summary>
    public static Order FromQuotation(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        Quotation quotation,
        string? notes,
        MemberId convertedBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        if (quotation.BillingAccount is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing account before it can be converted to an order.");
        }

        var header = new OrderHeader(
            quotation.ClientId,
            quotation.AdvisorId,
            quotation.Currency,
            quotation.PaymentMethod,
            quotation.IsRetail,
            quotation.GlobalScaleFloor,
            quotation.CustomerWithRetention,
            quotation.CustomerVatSurplus);
        var parties = new QuotationParties(
            quotation.Billing?.ToDetails(),
            quotation.Shipping?.ToDetails(),
            quotation.BillingUsesBusinessName,
            quotation.IsStorePickup,
            quotation.BillsToFinalConsumer,
            quotation.PartyWithRetention,
            quotation.PartyVatSurplus);
        var order = new Order(
            id, tenantId, orderNumber, OrderSource.Quotation, quotation.Id, null, header, parties,
            quotation.BillingAccount, paymentStatus: null, notes ?? quotation.Notes, convertedBy, occurredAt);
        // Copia tal cual, incluida la tasa: la cotización ya validó el envío al fijarlo, y el pedido nace con
        // sus mismos totales.
        order.AssignShipping(quotation.ShippingDetails);
        // La regla de la cotización, antes de copiar las líneas: el pedido tiene los mismos centavos.
        order._priceRoundingDecimals = quotation.PriceRounding.Decimals;
        order._priceRoundingMode = quotation.PriceRounding.Mode;
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var item in quotation.Items.OrderBy(item => item.Position))
        {
            var copy = OrderItem.Create(
                OrderItemId.New(), order.Id, item.VariantId, item.ProductId, item.Quantity, item.UnitPrice, item.DiscountPercentage,
                item.TaxPercentage, order._items.Count + 1, order.Currency, order.PriceRounding, occurredAt);
            copy.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, order.Currency, order.PriceRounding, occurredAt);
            if (item.ProductCode is not null)
            {
                copy.CaptureProduct(new QuotationProductSnapshot(item.ProductCode, item.ProductName ?? string.Empty));
            }

            order._items.Add(copy);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, convertedBy, occurredAt);
        order.PaymentStatus = order.CalculatedPaymentStatus();
        return order;
    }

    public static Order CreateManual(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        ShippingDetails shipping,
        IReadOnlyCollection<OrderLineInput> lines,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        return Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus: null, notes, createdBy, proofs, occurredAt);
    }

    /// <summary>
    /// El cálculo previo del pedido manual: las mismas reglas y la misma aritmética que
    /// <see cref="CreateManual"/>, pero la cuenta de cobro es opcional. Sin ella, el pedido se precia en
    /// <see cref="OrderHeader.Currency"/> (la moneda por defecto del tenant), igual que una cotización sin
    /// cuenta. Existe aparte, y no como un null aceptado por <see cref="CreateManual"/>, para que el
    /// pedido que se guarda no pueda nacer sin cuenta por descuido: el resultado de este método no se
    /// guarda nunca.
    /// </summary>
    public static Order Preview(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        ShippingDetails shipping,
        IReadOnlyCollection<OrderLineInput> lines,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt) =>
        Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus: null, notes, createdBy, proofs, occurredAt);

    /// <summary>Un pedido que trae una integración (Shopify y las que vengan). La elegibilidad del cliente
    /// —completo o incompleto— vive en Application: el dominio sólo exige que haya un id.</summary>
    public static Order CreateExternal(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        IReadOnlyCollection<OrderLineInput> lines,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt,
        OrderSource source,
        string externalReference,
        ShippingDetails shipping,
        string? externalKey,
        string? externalShippingTitle)
    {
        if (source is OrderSource.Quotation or OrderSource.Manual or OrderSource.Store)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid,
                "An external order needs an external source.");
        }

        if (string.IsNullOrWhiteSpace(externalReference))
        {
            throw new QuotationsDomainException(
                "order.order.external_reference_required",
                "An external order needs its reference in the source system.");
        }

        ArgumentNullException.ThrowIfNull(billingAccount);
        var key = NormalizeExternalKey(externalKey);
        // El envío lo trae la plataforma (flete §10): pasa por las mismas reglas de coherencia que el manual, pero
        // no se valida contra la transportadora ni contra los modos que el tenant tenga habilitados (D-F18).
        var order = Build(
            id, tenantId, orderNumber, source, externalReference, header, parties, billingAccount,
            shipping, lines, paymentStatus, notes, createdBy, proofs, occurredAt);
        order.ExternalKey = key;
        order.ExternalShippingTitle = NormalizeExternalShippingTitle(externalShippingTitle);
        return order;
    }

    /// <summary>
    /// El pedido que nace directo del checkout de la tienda pública (spec 2026-11-09). Se guarda con el cliente
    /// ya resuelto en <paramref name="header"/>, entregado (nunca recogida), con las dos partes, pago pendiente
    /// y sin comprobantes. Pasa por <see cref="EnsureCreatable"/> sin cambios: el correo de la tienda es
    /// obligatorio, así que la regla completa de partes aplica tal cual. No hay solicitud detrás, así que no
    /// lleva referencia externa; la clave sale del <c>clientRequestId</c> del checkout y hace idempotente el
    /// reintento del navegador.
    /// </summary>
    public static Order CreateFromStore(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        IReadOnlyCollection<OrderLineInput> lines,
        string? notes,
        MemberId createdBy,
        DateTimeOffset occurredAt,
        string externalKey,
        Guid? buyerAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        var key = NormalizeExternalKey(externalKey);
        var order = Build(
            id, tenantId, orderNumber, OrderSource.Store, externalReference: null, header, parties, billingAccount,
            ShippingDetails.None, lines, OrderPaymentStatus.PaymentPending, notes, createdBy, [], occurredAt);
        order.ExternalKey = key;
        order.BuyerAccountId = buyerAccountId;
        return order;
    }

    /// <summary>
    /// El cálculo de la tienda para el carrito (spec de la tienda §5.2): los totales con el mismo motor que un
    /// pedido, pero sin cliente, sin partes, con los indicadores del cliente apagados y sin cuenta de cobro,
    /// así que no hay retención ni excedente de IVA. <b>Nunca se guarda</b>. Es el único camino donde
    /// <see cref="ClientId"/> puede ser vacío, y salta <c>order.order.client_required</c> con una bandera
    /// privada del constructor para que ningún otro camino pueda hacerlo.
    /// </summary>
    public static Order QuoteForStore(
        Guid tenantId,
        string currency,
        bool isRetail,
        IReadOnlyCollection<OrderLineInput> lines,
        DateTimeOffset occurredAt)
    {
        var nobody = new MemberId(Guid.Empty);
        var header = new OrderHeader(Guid.Empty, nobody, currency, null, isRetail, null, false, false);
        return Build(
            OrderId.New(), tenantId, "STORE-QUOTE", OrderSource.Store, null, header, QuotationParties.Empty, null,
            ShippingDetails.None, lines, OrderPaymentStatus.PaymentPending, null, nobody, [], occurredAt,
            allowNoClient: true);
    }

    private static Order Build(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderSource source,
        string? externalReference,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        ShippingDetails shipping,
        IReadOnlyCollection<OrderLineInput> lines,
        OrderPaymentStatus? paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt,
        bool allowNoClient = false)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(shipping);
        var order = new Order(
            id, tenantId, orderNumber, source, null, externalReference, header, parties, billingAccount,
            paymentStatus, notes, createdBy, occurredAt, allowNoClient);
        order.AssignShipping(shipping.Validated(order.IsStorePickup, order.Currency, "order.order"));
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var line in lines)
        {
            order.AddItemCore(
                OrderItemId.New(), line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.TaxPercentage, line.DiscountOrigin, line.Product, occurredAt);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, createdBy, occurredAt);
        if (paymentStatus is null)
        {
            order.PaymentStatus = order.CalculatedPaymentStatus();
        }

        return order;
    }

    // Lo que EnsureConvertibleToOrder exige a la cotización, trasladado al pedido (spec §4.4).
    private void EnsureCreatable()
    {
        if (_items.Count == 0)
        {
            throw new QuotationsDomainException(
                "order.order.items_required",
                "An order needs at least one product.");
        }

        EnsureBillingRules(Billing, PartyWithRetention, PartyVatSurplus);

        if (Shipping is { } shipping && !shipping.IsComplete)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyIncomplete,
                "The shipping party must have all its fields filled.");
        }
    }

    // Las reglas de facturación del alta, compartidas con SetBilling para que editar no pueda dejar un
    // pedido que crear habría rechazado (spec 2026-10-26).
    private static void EnsureBillingRules(OrderParty? billing, bool? partyWithRetention, bool? partyVatSurplus)
    {
        if (billing is null)
        {
            return;
        }

        if (!billing.IsComplete)
        {
            throw new QuotationsDomainException(
                "order.billing.party_incomplete",
                "The billing party must have all its fields filled.");
        }

        if (!string.IsNullOrWhiteSpace(billing.Name)
            && (string.IsNullOrWhiteSpace(billing.IdentificationNumber) || billing.IdentificationType is null))
        {
            throw new QuotationsDomainException(
                "order.billing.identification_required",
                "A billing party with its own name requires an identification number.");
        }

        if (partyWithRetention is null || partyVatSurplus is null)
        {
            throw new QuotationsDomainException(
                "order.billing.tax_profile_required",
                "The billing party must state whether it applies withholding tax and VAT surplus.");
        }
    }

    /// <summary>
    /// Cambia a quién se le factura en un pedido pendiente (spec 2026-10-26): la fila de facturación, el
    /// consumidor final, la razón social y el perfil fiscal de la fila. La fila existente se <b>modifica</b>
    /// y no se reemplaza: borrar y re-crear en el mismo guardado choca con <c>IX_order_parties_order_role</c>.
    /// Devuelve <c>false</c>, sin tocar la versión, si nada cambió. Con <paramref name="enforceCompleteness"/> en
    /// <c>false</c> (el cálculo previo, que se hace mientras la persona teclea) se omiten las exigencias de fila
    /// completa, identificación y perfil fiscal; la consistencia con el consumidor final sigue.
    /// </summary>
    public bool SetBilling(
        QuotationPartyDetails? billing,
        bool billsToFinalConsumer,
        bool billingUsesBusinessName,
        bool? partyWithRetention,
        bool? partyVatSurplus,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        bool enforceCompleteness = true)
    {
        EnsureEditable("The billing can only change while the order is editable.");
        EnsureBillingIsConsistent(billsToFinalConsumer, billing is not null, billingUsesBusinessName);

        // La fila candidata se arma aparte para validar lo normalizado antes de mutar nada.
        var candidate = billing is null ? null : OrderParty.Create(Id, QuotationPartyRole.Billing, billing);
        var withRetention = billing is null ? null : partyWithRetention;
        var vatSurplus = billing is null ? null : partyVatSurplus;
        if (enforceCompleteness)
        {
            EnsureBillingRules(candidate, withRetention, vatSurplus);
        }

        var current = Billing;
        if (current?.ToDetails() == candidate?.ToDetails()
            && (current is null) == (candidate is null)
            && BillsToFinalConsumer == billsToFinalConsumer
            && BillingUsesBusinessName == billingUsesBusinessName
            && PartyWithRetention == withRetention
            && PartyVatSurplus == vatSurplus)
        {
            return false;
        }

        if (candidate is null)
        {
            if (current is not null)
            {
                _parties.Remove(current);
            }
        }
        else if (current is not null)
        {
            current.Apply(candidate.ToDetails());
        }
        else
        {
            _parties.Add(candidate);
        }

        BillsToFinalConsumer = billsToFinalConsumer;
        BillingUsesBusinessName = billingUsesBusinessName;
        PartyWithRetention = withRetention;
        PartyVatSurplus = vatSurplus;
        Touch(updatedBy, occurredAt);
        return true;
    }

    public void AddItem(
        OrderItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable("Products can only be added while the order is editable.");
        AddItemCore(
            itemId, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            QuotationDiscountOrigin.Own, null, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    private void AddItemCore(
        OrderItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        QuotationDiscountOrigin origin,
        QuotationProductSnapshot? product,
        DateTimeOffset occurredAt)
    {
        // Una línea por variante (spec 2026-10-20 §4): dos tallas del mismo producto son dos líneas. El
        // código de error se conserva: es contrato con el frontend.
        if (_items.Any(existing => existing.VariantId == variantId))
        {
            throw new QuotationsDomainException(
                "order.item.duplicate_product",
                "La variante ya está en el pedido.");
        }

        var item = OrderItem.Create(
            itemId, Id, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            _items.Count + 1, Currency, PriceRounding, occurredAt);
        if (origin != QuotationDiscountOrigin.Own)
        {
            item.ApplyDiscount(discountPercentage, origin, Currency, PriceRounding, occurredAt);
        }

        if (product is not null)
        {
            item.CaptureProduct(product);
        }

        _items.Add(item);
    }

    public void UpdateItemQuantity(
        OrderItemId itemId,
        decimal quantity,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable("Products can only be edited while the order is editable.");
        FindItem(itemId).UpdateQuantity(quantity, discountPercentage, taxPercentage, Currency, PriceRounding, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(OrderItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable("Products can only be removed while the order is editable.");
        if (_items.Count == 1)
        {
            throw new QuotationsDomainException(
                "order.item.last_item_required",
                "The last remaining item cannot be removed from the order.");
        }

        _items.Remove(FindItem(itemId));
        Touch(updatedBy, occurredAt);
    }

    public void SetIsRetail(bool isRetail, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable("The pricing can only change while the order is editable.");
        IsRetail = isRetail;
        if (isRetail)
        {
            GlobalScaleFloor = null;
        }

        Touch(updatedBy, occurredAt);
    }

    public void SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable("The pricing can only change while the order is editable.");
        EnsureGlobalScaleFloorAllowed(floor, IsRetail);
        GlobalScaleFloor = floor;
        Touch(updatedBy, occurredAt);
    }

    /// <summary>
    /// Cambia el envío de un pedido pendiente (spec 2026-10-17 flete §5.4). No cambia recoger o enviar: el
    /// pedido no edita sus partes. Prender la contraentrega exige un pago que todavía no empezó —sin
    /// comprobantes y <see cref="OrderPaymentStatus.PaymentPending"/>— (D-F14); ya prendida, se conserva aunque
    /// llegue el comprobante del giro.
    /// </summary>
    public void SetShipping(ShippingDetails shipping, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        EnsureEditable("The shipping can only change while the order is editable.");
        var validated = shipping.Validated(IsStorePickup, Currency, "order.order");
        if (validated.CashOnDelivery && !CashOnDelivery
            && (_paymentProofs.Count > 0 || PaymentStatus != OrderPaymentStatus.PaymentPending))
        {
            throw CashOnDeliveryPendingOnly();
        }

        AssignShipping(validated);
        Touch(updatedBy, occurredAt);
    }

    private void AssignShipping(ShippingDetails shipping)
    {
        CarrierId = shipping.CarrierId;
        CarrierName = shipping.CarrierName;
        FreightChargeMode = shipping.ChargeMode;
        FreightAmount = shipping.FreightAmount;
        FreightTaxPercentage = shipping.FreightTaxPercentage;
        CashOnDelivery = shipping.CashOnDelivery;
    }

    // Spec §6: un pedido contraentrega se cobra en la puerta, así que nace sin pago registrado: ni otro estado
    // de pago ni comprobantes, aunque el estado sea pendiente. Es la misma regla que SetShipping (D-F14, R16).
    private void EnsureCashOnDeliveryIsPending(IReadOnlyCollection<OrderPaymentProofInput> proofs)
    {
        if (CashOnDelivery && (proofs.Count > 0 || PaymentStatus != OrderPaymentStatus.PaymentPending))
        {
            throw CashOnDeliveryPendingOnly();
        }
    }

    private static QuotationsDomainException CashOnDeliveryPendingOnly() =>
        new(
            "order.order.cash_on_delivery_pending_only",
            "A cash on delivery order cannot have a payment registered yet.");

    public void ApplyGroupDiscounts(
        IReadOnlyDictionary<OrderItemId, QuotationItemDiscount> discounts,
        DateTimeOffset occurredAt)
    {
        foreach (var item in _items)
        {
            if (discounts.TryGetValue(item.Id, out var discount))
            {
                item.ApplyDiscount(discount.Percentage, discount.Origin, Currency, PriceRounding, occurredAt);
            }
        }

        RecalculateTotals();
    }

    public void RefreshCustomerTaxProfile(bool customerWithRetention, bool customerVatSurplus)
    {
        if (!IsEditable)
        {
            return;
        }

        if (CustomerWithRetention == customerWithRetention && CustomerVatSurplus == customerVatSurplus)
        {
            return;
        }

        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        RecalculateTotals();
    }

    public void CaptureProductSnapshots(IReadOnlyDictionary<Guid, QuotationProductSnapshot> products)
    {
        foreach (var item in _items)
        {
            if (products.TryGetValue(item.VariantId, out var product))
            {
                item.CaptureProduct(product);
            }
        }
    }

    public OrderItem FindItem(OrderItemId itemId) =>
        _items.FirstOrDefault(item => item.Id == itemId)
            ?? throw new QuotationsDomainException(
                "order.item.not_found",
                "The order item was not found.");

    /// <summary>
    /// Si el pedido se le puede mandar al cliente por WhatsApp. Enviar no cambia el pedido —no hay
    /// estado «enviado»—, así que esto no protege una transición: existe para que el caso de uso lo
    /// compruebe **antes** de generar el PDF y de entregarle el mensaje a WhatsApp, que no se deshacen.
    ///
    /// Sólo frena al anulado: un pedido anulado que le llega al cliente se lee como vigente. Abierto,
    /// archivado, pagado o facturado sí se mandan: el cliente puede necesitar el documento en cualquiera.
    /// </summary>
    public void EnsureSendable()
    {
        // Mismo código que anular dos veces: para la pantalla es el mismo hecho, el pedido ya no rige.
        if (Status == OrderStatus.Cancelled)
        {
            throw new QuotationsDomainException(
                "order.order.already_cancelled",
                "A cancelled order cannot be sent.");
        }
    }

    /// <summary>Lo que <see cref="MarkPaid"/> exige, para que el caso de uso lo compruebe antes de las validaciones
    /// de datos de facturación. Orden: abierto, no pagado y, si el tenant lo pide, con comprobante.</summary>
    public void EnsureCanMarkPaid(bool requirePaymentProof)
    {
        EnsureOpen("Only an open order can be marked as paid.");
        if (PaymentStatus == OrderPaymentStatus.FullPaymentReceived)
        {
            throw new QuotationsDomainException(OrderStateErrors.AlreadyPaid, "The order is already paid.");
        }

        if (requirePaymentProof && _paymentProofs.Count == 0)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.PaymentProofRequired,
                "The tenant requires a payment proof to mark an order as paid.");
        }
    }

    /// <summary>«Marcar como pagado» (spec 2026-11-10 §3.2): la única forma, fuera de la plataforma, de llegar a
    /// <see cref="OrderPaymentStatus.FullPaymentReceived"/>.</summary>
    public void MarkPaid(MemberId markedBy, bool requirePaymentProof, DateTimeOffset occurredAt)
    {
        EnsureCanMarkPaid(requirePaymentProof);
        PaymentStatus = OrderPaymentStatus.FullPaymentReceived;
        PaidMarkedBy = markedBy;
        PaidMarkedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Deshace el pagado: el pago vuelve al valor calculado con los comprobantes. Sólo si el pedido no
    /// está preparado ni facturado.</summary>
    public void UnmarkPaid(DateTimeOffset occurredAt)
    {
        EnsureOpen("Only an open order can have its payment unmarked.");
        if (PaymentStatus != OrderPaymentStatus.FullPaymentReceived)
        {
            throw new QuotationsDomainException(OrderStateErrors.NotPaid, "The order is not paid.");
        }

        EnsureNotPrepared("A prepared order cannot have its payment unmarked.");
        EnsureNotInvoiced("An invoiced order cannot have its payment unmarked.");
        PaymentStatus = CalculatedPaymentStatus();
        PaidMarkedBy = null;
        PaidMarkedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>«Marcar como preparado» (spec 2026-11-10 §3.3): pagado o no, porque un contraentrega se despacha
    /// antes del cobro. Congela el pedido.</summary>
    public void Prepare(MemberId preparedBy, DateTimeOffset occurredAt)
    {
        EnsureOpen("Only an open order can be prepared.");
        EnsureNotPrepared("The order is already prepared.");
        FulfillmentStatus = OrderFulfillmentStatus.Prepared;
        PreparedBy = preparedBy;
        PreparedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void UndoPreparation(DateTimeOffset occurredAt)
    {
        EnsureOpen("Only an open order can have its preparation undone.");
        if (!IsPrepared)
        {
            throw new QuotationsDomainException(OrderStateErrors.NotPrepared, "The order is not prepared.");
        }

        EnsureNotInvoiced("An invoiced order cannot have its preparation undone.");
        FulfillmentStatus = OrderFulfillmentStatus.NotPrepared;
        PreparedBy = null;
        PreparedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Archiva a mano o por el archivado automático (<paramref name="archivedBy"/> null cuando no hay
    /// persona). No quita ni pone ninguna otra marca (spec 2026-11-10 §3.5).</summary>
    public void Archive(MemberId? archivedBy, DateTimeOffset occurredAt)
    {
        EnsureNotCancelled("A cancelled order cannot be archived.");
        if (Status == OrderStatus.Archived)
        {
            throw new QuotationsDomainException(OrderStateErrors.AlreadyArchived, "The order is already archived.");
        }

        Status = OrderStatus.Archived;
        ArchivedBy = archivedBy;
        ArchivedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void Unarchive(DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Archived)
        {
            throw new QuotationsDomainException(OrderStateErrors.NotArchived, "Only an archived order can be unarchived.");
        }

        Status = OrderStatus.Open;
        ArchivedBy = null;
        ArchivedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>El archivado automático: archiva y devuelve <c>true</c> sólo si el pedido está abierto, pagado y
    /// preparado; si no, devuelve <c>false</c> sin tocar nada.</summary>
    public bool ArchiveIfSettled(MemberId? archivedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Open || PaymentStatus != OrderPaymentStatus.FullPaymentReceived || !IsPrepared)
        {
            return false;
        }

        Archive(archivedBy, occurredAt);
        return true;
    }

    /// <summary>La anulación de la consola. Orden: facturado, anulado, archivado, preparado y, al final, el motivo.</summary>
    public void Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)
    {
        // Spec 2026-10-05, decisión 2: la factura ya existe fuera de QEP y anular el pedido no la
        // anula. Va antes que already_cancelled y que el motivo: un facturado nunca se anula, así que
        // pedir el motivo primero mandaría a corregir algo que no destraba nada.
        EnsureNotInvoiced("An invoiced order cannot be cancelled; revert the invoicing first.");
        EnsureNotCancelled("The order is already cancelled.");
        if (Status == OrderStatus.Archived)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.AlreadyArchived, "An archived order cannot be cancelled; unarchive it first.");
        }

        EnsureNotPrepared("A prepared order cannot be cancelled; undo the preparation first.");
        ApplyCancellation(cancelledBy, NormalizeCancellationReason(reason), occurredAt);
    }

    /// <summary>
    /// La anulación que llega de la plataforma (spec 2026-11-10 §3.1, owner): gana sobre preparado y archivado y
    /// conserva sus marcas, para que se vea que hay un paquete despachado que recuperar. Facturado sigue frenando.
    /// </summary>
    public void CancelFromPlatform(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)
    {
        EnsureNotInvoiced("An invoiced order cannot be cancelled; revert the invoicing first.");
        EnsureNotCancelled("The order is already cancelled.");
        ApplyCancellation(cancelledBy, NormalizeCancellationReason(reason), occurredAt);
    }

    private void ApplyCancellation(MemberId cancelledBy, string reason, DateTimeOffset occurredAt)
    {
        Status = OrderStatus.Cancelled;
        CancelledBy = cancelledBy;
        CancelledAt = occurredAt;
        CancellationReason = reason;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Enlaza el pedido a una cuenta de comprador sólo si todavía no tiene una. Devuelve <c>false</c> sin
    /// cambiar nada cuando ya estaba enlazado. No toca el estado ni la versión: el enlace no es una edición del
    /// pedido.</summary>
    public bool LinkBuyerAccount(Guid buyerAccountId)
    {
        if (BuyerAccountId is not null)
        {
            return false;
        }

        BuyerAccountId = buyerAccountId;
        return true;
    }

    public void CancelByBuyer(Guid buyerAccountId, DateTimeOffset occurredAt)
    {
        if (BuyerAccountId != buyerAccountId || !CanBeCancelledByBuyer)
        {
            throw new QuotationsDomainException(
                BuyerOrderErrors.NotCancellableByBuyer,
                "The order cannot be cancelled by this buyer.");
        }

        Status = OrderStatus.Cancelled;
        CancelledBy = null;
        CancelledAt = occurredAt;
        CancellationReason = BuyerOrderErrors.CancellationReason;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Lo que <see cref="Invoice"/> exige, para que el caso de uso lo compruebe antes de las validaciones de
    /// datos de facturación. No exige preparado (spec 2026-11-10 §3.4).</summary>
    public void EnsureCanInvoice(bool requireFullPayment)
    {
        EnsureNotCancelled("A cancelled order cannot be invoiced.");
        EnsureNotInvoiced("The order is already invoiced.");
        if (requireFullPayment && PaymentStatus != OrderPaymentStatus.FullPaymentReceived)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.NotPaid, "The tenant requires the order to be paid before it is invoiced.");
        }
    }

    /// <summary>Marca el pedido como facturado. Es una marca, no un estado: <see cref="Status"/> no cambia.</summary>
    public void Invoice(MemberId invoicedBy, bool requireFullPayment, DateTimeOffset occurredAt)
    {
        EnsureCanInvoice(requireFullPayment);
        InvoicedBy = invoicedBy;
        InvoicedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)
    {
        if (!IsInvoiced)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.NotInvoiced,
                "Only an invoiced order can have its invoicing reverted.");
        }

        InvoicedBy = null;
        InvoicedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Suma comprobantes y corrige los que ya estaban (spec 2026-11-10 §3.2). El pago lo calcula el sistema: un
    /// pedido pagado sigue pagado, y uno sin pagar queda con lo que digan los comprobantes, nunca pagado. Devuelve el
    /// cambio del pago, o <c>null</c> si no cambió.</summary>
    public OrderPaymentStatusChange? AddPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        string? notes,
        MemberId uploadedBy,
        DateTimeOffset occurredAt,
        IReadOnlyCollection<OrderPaymentProofAmountUpdate>? updatedProofs = null)
    {
        updatedProofs ??= [];
        EnsureEditable("Payment proofs can only be added to an editable order.");

        if (proofs.Count == 0 && updatedProofs.Count == 0)
        {
            throw new QuotationsDomainException(
                "order.order.payment_proof_required",
                "At least one payment proof is required.");
        }

        foreach (var update in updatedProofs)
        {
            var proof = _paymentProofs.FirstOrDefault(candidate => candidate.Id == update.ProofId)
                ?? throw new QuotationsDomainException(
                    "order.payment_proof.not_found",
                    $"Payment proof '{update.ProofId}' was not found on this order.");
            proof.UpdateAmount(update.Amount);
            proof.UpdatePaidOn(update.PaidOn);
            if (update.NewFileId is { } newFileId)
            {
                proof.UpdateFile(newFileId, update.NewPublicStorageKey);
            }
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(OrderPaymentProof.Create(
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt, proof.PaidOn));
        }

        var from = PaymentStatus;
        if (from != OrderPaymentStatus.FullPaymentReceived)
        {
            PaymentStatus = CalculatedPaymentStatus();
        }

        Notes = NormalizeNotes(notes);
        UpdatedAt = occurredAt;
        Version++;
        return from == PaymentStatus ? null : new OrderPaymentStatusChange(from, PaymentStatus, NetTotal, NetTotal);
    }

    /// <summary>
    /// El pago de un pedido manual que todavía no se guardó, otra vez desde sus comprobantes. El alta y su cálculo
    /// previo lo llaman después de <c>OrderPricingRecalculation</c>, cuyos descuentos de grupo y redondeo mueven el
    /// neto que <see cref="CreateManual"/> ya había comparado. No es una edición: no toca <see cref="Version"/>.
    /// Pagado no se toca: el sistema nunca sube ni baja una marca de una persona acá.
    /// </summary>
    public void RecalculateDraftPaymentStatus()
    {
        if (Source != OrderSource.Manual || PaymentStatus == OrderPaymentStatus.FullPaymentReceived)
        {
            return;
        }

        PaymentStatus = CalculatedPaymentStatus();
    }

    /// <summary>Vuelve a mirar el pago después de una edición que pudo mover el neto (spec 2026-11-10 §3.2).
    /// <paramref name="previousNetTotal"/> es el neto de antes de la edición.
    ///
    /// Sin pagar, el pago es el que digan los comprobantes, nunca pagado. Pagado, queda pagado salvo un caso: hay
    /// comprobantes, la edición subió el neto (comparado como lo muestra la moneda) y lo consignado ya no lo cubre;
    /// ahí baja a <see cref="OrderPaymentStatus.PartialPaymentReceived"/> y se limpia la marca de pagado. Un pedido
    /// pagado sin comprobantes se dio por pagado a mano y ninguna edición lo baja.
    ///
    /// Devuelve el cambio, o <c>null</c> si el pago no cambió.</summary>
    public OrderPaymentStatusChange? RecalculatePaymentStatus(decimal previousNetTotal, DateTimeOffset occurredAt)
    {
        EnsureEditable("The payment status can only be recalculated on an editable order.");
        var from = PaymentStatus;
        if (from != OrderPaymentStatus.FullPaymentReceived)
        {
            PaymentStatus = CalculatedPaymentStatus();
        }
        else if (_paymentProofs.Count > 0
            && DisplayedNetTotal > RoundAsDisplayed(previousNetTotal)
            && ProofsTotal < DisplayedNetTotal)
        {
            PaymentStatus = OrderPaymentStatus.PartialPaymentReceived;
            PaidMarkedBy = null;
            PaidMarkedAt = null;
        }

        UpdatedAt = occurredAt;
        Version++;
        return from == PaymentStatus
            ? null
            : new OrderPaymentStatusChange(from, PaymentStatus, previousNetTotal, NetTotal);
    }

    /// <summary>
    /// El estado de pago que avisa la plataforma (spec de Shopify §6.3), sólo si el pedido no tiene comprobantes
    /// propios; con comprobantes, el estado lo gobiernan ellos. La plataforma manda sobre sus pedidos: un pedido
    /// preparado o archivado también lo recibe (un contraentrega se prepara y se cobra después). Sólo lo rechazan
    /// uno anulado o facturado. No toca la marca de pagado: no lo marcó una persona.
    ///
    /// Con <paramref name="autoArchive"/> (el ajuste del tenant), un pedido que queda pagado y ya estaba preparado se
    /// archiva sin persona detrás. Devuelve <c>true</c> si lo archivó.
    /// </summary>
    public bool SyncExternalPaymentStatus(OrderPaymentStatus status, bool autoArchive, DateTimeOffset occurredAt)
    {
        if (Status == OrderStatus.Cancelled || IsInvoiced)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.NotEditable,
                "The payment status cannot be synced on a cancelled or invoiced order.");
        }

        if (_paymentProofs.Count > 0)
        {
            throw new QuotationsDomainException(
                "order.order.payment_has_proofs",
                "The payment status is governed by the attached proofs.");
        }

        if (PaymentStatus == status)
        {
            return false;
        }

        PaymentStatus = status;
        UpdatedAt = occurredAt;
        Version++;
        return autoArchive && ArchiveIfSettled(archivedBy: null, occurredAt);
    }

    public void RemovePaymentProof(OrderPaymentProofId proofId, DateTimeOffset occurredAt)
    {
        EnsureEditable("Payment proofs can only be removed from an editable order.");

        var proof = _paymentProofs.FirstOrDefault(candidate => candidate.Id == proofId)
            ?? throw new QuotationsDomainException(
                "order.payment_proof.not_found",
                $"Payment proof '{proofId}' was not found on this order.");
        _paymentProofs.Remove(proof);
        UpdatedAt = occurredAt;
        Version++;
    }

    public void AttachPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable("Payment proofs can only be added to an editable order.");
        if (proofs.Count == 0)
        {
            return;
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(OrderPaymentProof.Create(
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt, proof.PaidOn));
        }

        UpdatedAt = occurredAt;
        Version++;
    }

    public void CorrectPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofAmountUpdate> updates, DateTimeOffset occurredAt)
    {
        EnsureEditable("Payment proofs can only be corrected on an editable order.");
        if (updates.Count == 0)
        {
            return;
        }

        var targets = updates
            .Select(update => (
                Update: update,
                Proof: _paymentProofs.FirstOrDefault(candidate => candidate.Id == update.ProofId)
                    ?? throw new QuotationsDomainException(
                        "order.payment_proof.not_found",
                        $"Payment proof '{update.ProofId}' was not found on this order.")))
            .ToArray();
        foreach (var (update, proof) in targets)
        {
            proof.UpdateAmount(update.Amount);
            proof.UpdatePaidOn(update.PaidOn);
            if (update.NewFileId is { } newFileId)
            {
                proof.UpdateFile(newFileId, update.NewPublicStorageKey);
            }
        }

        UpdatedAt = occurredAt;
        Version++;
    }

    public bool UpdateNotes(string? notes, DateTimeOffset occurredAt)
    {
        EnsureEditable("The notes can only be edited on an editable order.");
        var normalized = NormalizeNotes(notes);
        if (string.Equals(normalized, Notes, StringComparison.Ordinal))
        {
            return false;
        }

        Notes = normalized;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    /// <summary>
    /// Spec 2026-10-26 §4.4: agrega la parte de envío que Shopify no pudo completar al crear el pedido (la ciudad
    /// no se reconocía). <c>true</c> si la agregó; <c>false</c> si ya estaba esa misma parte, en esa ciudad o en otra (un
    /// reintento: como un pedido no edita sus partes, desvío S1, la misma parte en otra ciudad la puso un intento anterior con
    /// la ciudad que el sinónimo tenía entonces; el consumidor lee la que quedó en <see cref="Shipping"/>).
    /// Quita de las notas toda línea que empiece por <paramref name="notesLinePrefix"/> (ordinal): la dirección en
    /// crudo que se anotó mientras no había parte. Sólo pedidos externos pendientes, sin recogida en tienda y sin
    /// otra parte de envío; la parte debe quedar completa. No recalcula totales.
    /// </summary>
    public bool AttachExternalShipping(QuotationPartyDetails shipping, string notesLinePrefix, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        ArgumentException.ThrowIfNullOrEmpty(notesLinePrefix);
        EnsureEditable("The shipping party can only be attached while the order is editable.");
        if (!IsExternal)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only an external order takes its shipping party from the platform.");
        }

        if (IsStorePickup)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.StorePickup, "An order picked up at the store has no shipping party.");
        }

        var candidate = OrderParty.Create(Id, QuotationPartyRole.Shipping, shipping);
        if (Shipping is { } current)
        {
            return SameExceptCity(current.ToDetails(), candidate.ToDetails())
                ? false
                : throw new QuotationsDomainException(
                    ExternalOrderErrors.ShippingPartyPresent, "The order already has another shipping party.");
        }

        if (!candidate.IsComplete)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyIncomplete, "The shipping party must have all its fields filled.");
        }

        _parties.Add(candidate);
        Notes = NormalizeNotes(string.Join(
            '\n',
            (Notes ?? string.Empty).Split('\n').Where(line => !line.StartsWith(notesLinePrefix, StringComparison.Ordinal))));
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    /// <summary>
    /// Spec 2026-10-26 §4.6: cambia sólo el departamento y la ciudad de la parte de envío, y sólo si sigue siendo
    /// la que se guardó (<paramref name="expected"/>) salvo la ciudad: si alguien tocó otro campo, lanza
    /// <c>order.order.shipping_party_changed</c>. <c>true</c> si cambió; <c>false</c> si la parte ya no está en la ciudad de
    /// <paramref name="expected"/> (un reintento: la llevó un intento anterior, a ésta o a la que el sinónimo tenía entonces, y el
    /// consumidor cayó antes de actualizar su huella; lee la que quedó en <see cref="Shipping"/>).
    /// </summary>
    public bool CorrectExternalShippingCity(
        QuotationPartyDetails expected, Guid departmentId, Guid cityId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureEditable("The shipping city can only be corrected while the order is editable.");
        if (Shipping is not { } party || !SameExceptCity(party.ToDetails(), expected))
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyChanged, "The shipping party is no longer the one the platform sent.");
        }

        if (party.ToDetails() != expected || (party.DepartmentId == departmentId && party.CityId == cityId))
        {
            return false;
        }

        party.Apply(party.ToDetails() with { DepartmentId = departmentId, CityId = cityId });
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    // Spec 2026-11-10 §3.2: la regla de US-14 («al menos un comprobante salvo pago pendiente») ya no existe: el pago
    // de un pedido interno sale de los comprobantes, así que sin ellos simplemente queda pendiente.
    private void AddProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)
    {
        foreach (var proof in proofs)
        {
            _paymentProofs.Add(OrderPaymentProof.Create(
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt, proof.PaidOn));
        }
    }

    // Lista positiva a propósito: la tienda se precia y se cobra como un pedido interno (su pago pendiente lo
    // confirma una persona en QEP), así que una fuente nueva no hereda por descarte el trato de integración.
    private bool IsExternal => Source is OrderSource.Shopify;

    // Spec 2026-10-26 y desvío S1: un pedido no edita sus partes, así que una parte de envío igual salvo el departamento y la
    // ciudad es la que puso la plataforma (en otra ciudad del mismo sinónimo), no una de otra persona.
    private static bool SameExceptCity(QuotationPartyDetails party, QuotationPartyDetails other) =>
        party with { DepartmentId = other.DepartmentId, CityId = other.CityId } == other;

    private void EnsureEditable(string message)
    {
        if (!IsEditable)
        {
            throw new QuotationsDomainException(ExternalOrderErrors.NotEditable, message);
        }
    }

    private void EnsureOpen(string message)
    {
        if (Status != OrderStatus.Open)
        {
            throw new QuotationsDomainException(OrderStateErrors.NotOpen, message);
        }
    }

    private void EnsureNotPrepared(string message)
    {
        if (IsPrepared)
        {
            throw new QuotationsDomainException(OrderStateErrors.AlreadyPrepared, message);
        }
    }

    private void EnsureNotInvoiced(string message)
    {
        if (IsInvoiced)
        {
            throw new QuotationsDomainException(OrderStateErrors.AlreadyInvoiced, message);
        }
    }

    private void EnsureNotCancelled(string message)
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new QuotationsDomainException(OrderStateErrors.AlreadyCancelled, message);
        }
    }

    /// <summary>Lo que falta por cobrar contra el neto como lo muestra la moneda: positivo si falta, negativo si
    /// sobra (spec 2026-11-10 §3.2).</summary>
    public decimal Balance => DisplayedNetTotal - ProofsTotal;

    private decimal ProofsTotal => _paymentProofs.Sum(proof => proof.Amount);

    // El neto redondeado a lo que la moneda muestra: pesos enteros en COP, centavos en las demás (hotfix
    // 2026-10-19). La retención se redondea a centavos y le deja al neto una fracción de peso que la pantalla no
    // muestra —un neto de 116.529,37 se ve como $116.529—, así que quien cargaba lo que veía quedaba en pago
    // parcial por 37 centavos. Mismo redondeo que roundToCurrencyPrecision del frontend.
    private decimal DisplayedNetTotal => RoundAsDisplayed(NetTotal);

    private decimal RoundAsDisplayed(decimal amount) =>
        Math.Round(amount, Currency == "COP" ? 0 : 2, MidpointRounding.AwayFromZero);

    // Spec 2026-11-10 §3.2: lo que dicen los comprobantes, comparado contra el neto que la moneda muestra. Nunca
    // llega a FullPaymentReceived: eso sólo lo da «Marcar como pagado» o la plataforma.
    private OrderPaymentStatus CalculatedPaymentStatus()
    {
        var proofsTotal = ProofsTotal;
        return proofsTotal <= 0
            ? OrderPaymentStatus.PaymentPending
            : proofsTotal < DisplayedNetTotal
                ? OrderPaymentStatus.PartialPaymentReceived
                : OrderPaymentStatus.PendingVerification;
    }

    // Quién editó no se guarda por línea (el historial del pedido lo registra); el parámetro queda
    // por simetría con Quotation.Touch y para que los llamadores lean igual.
    private void Touch(MemberId updatedBy, DateTimeOffset occurredAt)
    {
        _ = updatedBy;
        RecalculateTotals();
        UpdatedAt = occurredAt;
        Version++;
    }

    private void RecalculateTotals()
    {
        var totals = DocumentTotals.Compute(
            _items.Select(item => item.Amounts),
            FreightAmounts.For(FreightChargeMode, FreightAmount, FreightTaxPercentage, Currency, PriceRounding),
            AppliesVatSurplus,
            AppliesRetention);
        Subtotal = totals.Subtotal;
        DiscountAmount = totals.DiscountAmount;
        TaxAmount = totals.TaxAmount;
        TaxPercentage = totals.TaxPercentage;
        Total = totals.Total;
        RetentionAmount = totals.RetentionAmount;
        NetTotal = totals.NetTotal;
        FreightSubtotal = totals.FreightSubtotal;
        FreightTaxAmount = totals.FreightTaxAmount;
    }

    private void AssignParties(QuotationParties parties)
    {
        if (parties.Billing is { } billing)
        {
            _parties.Add(OrderParty.Create(Id, QuotationPartyRole.Billing, billing));
        }

        if (!parties.IsStorePickup && parties.Shipping is { } shipping)
        {
            _parties.Add(OrderParty.Create(Id, QuotationPartyRole.Shipping, shipping));
        }
    }

    private static void EnsureBillingIsConsistent(bool billsToFinalConsumer, bool hasBillingRow, bool usesBusinessName)
    {
        if (billsToFinalConsumer && (hasBillingRow || usesBusinessName))
        {
            throw new QuotationsDomainException(
                "order.billing.final_consumer_conflict",
                "An order billed to the final consumer cannot carry its own billing party or bill to the customer's business name.");
        }
    }

    private static void EnsureGlobalScaleFloorAllowed(int? floor, bool isRetail)
    {
        if (isRetail && floor is not null)
        {
            throw new QuotationsDomainException(
                "order.retail.floor_not_allowed",
                "A retail order cannot use a global scale floor.");
        }

        if (floor is { } value && value < 1)
        {
            throw new QuotationsDomainException(
                "order.global_scale.floor_invalid",
                "The global scale floor must be a positive unit count.");
        }
    }

    private static string NormalizeOrderNumber(string orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            throw new QuotationsDomainException(
                "order.order.number_required",
                "The order number is required.");
        }

        var trimmed = orderNumber.Trim();
        return trimmed.Length > OrderNumberMaxLength
            ? throw new QuotationsDomainException(
                "order.order.number_too_long",
                $"The order number cannot exceed {OrderNumberMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizeExternalReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        return trimmed.Length > ExternalReferenceMaxLength
            ? throw new QuotationsDomainException(
                "order.order.external_reference_too_long",
                $"The external reference cannot exceed {ExternalReferenceMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizeExternalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        return trimmed.Length > ExternalKeyMaxLength
            ? throw new QuotationsDomainException(
                "order.order.external_key_too_long",
                $"The external key cannot exceed {ExternalKeyMaxLength} characters.")
            : trimmed;
    }

    // Es un dato informativo de la tienda: un título largo no debe tumbar la importación del pedido.
    private static string? NormalizeExternalShippingTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var trimmed = title.Trim();
        return trimmed.Length > ExternalShippingTitleMaxLength
            ? trimmed[..ExternalShippingTitleMaxLength].TrimEnd()
            : trimmed;
    }

    private static string? NormalizePaymentMethod(string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return null;
        }

        var trimmed = paymentMethod.Trim();
        return trimmed.Length > PaymentMethodMaxLength
            ? throw new QuotationsDomainException(
                "order.order.payment_method_too_long",
                $"The payment method cannot exceed {PaymentMethodMaxLength} characters.")
            : trimmed;
    }

    // P2: hereda Quotation.Notes, que no tiene tope; sólo se recorta.
    private static string? NormalizeNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

    // Decisión 4: obligatorio, a diferencia de las notas. El límite se mide recortado.
    private static string NormalizeCancellationReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new QuotationsDomainException(
                "order.order.cancellation_reason_required",
                "A reason is required to cancel an order.");
        }

        var trimmed = reason.Trim();
        return trimmed.Length > CancellationReasonMaxLength
            ? throw new QuotationsDomainException(
                "order.order.cancellation_reason_too_long",
                $"The cancellation reason cannot exceed {CancellationReasonMaxLength} characters.")
            : trimmed;
    }
}

/// <summary>Un comprobante de pago tal como lo manda el cliente, sin id: <see cref="Order"/>
/// asigna un <see cref="OrderPaymentProofId"/> nuevo a cada uno. <c>PublicStorageKey</c> es la clave
/// de la copia pública que el handler ya hizo (spec 2026-09-15, P5), o null si el comprobante queda
/// privado. <c>PaidOn</c> es la fecha del soporte.</summary>
public sealed record OrderPaymentProofInput(
    Guid FileId, decimal Amount, string? PublicStorageKey = null, DateOnly? PaidOn = null);

/// <summary>La corrección de un comprobante que ya existe: lleva el id del comprobante a corregir.
/// <paramref name="NewFileId"/> reemplaza el archivo (null cuando sólo se corrige el monto);
/// <paramref name="NewPublicStorageKey"/> es la copia pública ya publicada de ese archivo;
/// <paramref name="PaidOn"/> reemplaza la fecha del soporte y null la borra.</summary>
public sealed record OrderPaymentProofAmountUpdate(
    OrderPaymentProofId ProofId,
    decimal Amount,
    Guid? NewFileId = null,
    string? NewPublicStorageKey = null,
    DateOnly? PaidOn = null);
