namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido con datos propios (spec 2026-10-15): cliente, asesor, cuenta de cobro, partes, líneas y
/// totales. Nace de una cotización (<see cref="FromQuotation"/>, copia independiente), a mano
/// (<see cref="CreateManual"/>) o desde una integración (<see cref="CreateExternal"/>). Sólo mientras
/// sigue <see cref="OrderStatus.Pending"/> se editan sus líneas, partes y comprobantes. Agregado raíz
/// que incluye sus comprobantes de pago (<see cref="OrderPaymentProof"/>): nacen con él, pero mientras
/// sigue pendiente se le pueden sumar más y corregir el monto de los que ya tiene.
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
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        OrderNumber = NormalizeOrderNumber(orderNumber);
        Source = source;
        QuotationId = quotationId;
        ExternalReference = NormalizeExternalReference(externalReference);
        ClientId = header.ClientId == Guid.Empty
            ? throw new QuotationsDomainException("order.order.client_required", "The order client is required.")
            : header.ClientId;
        AdvisorId = header.AdvisorId;
        PaymentMethod = NormalizePaymentMethod(header.PaymentMethod);
        IsRetail = header.IsRetail;
        EnsureGlobalScaleFloorAllowed(header.GlobalScaleFloor, header.IsRetail);
        GlobalScaleFloor = header.GlobalScaleFloor;
        CustomerWithRetention = header.CustomerWithRetention;
        CustomerVatSurplus = header.CustomerVatSurplus;
        EnsureBillingIsConsistent(parties);
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
        Status = OrderStatus.Pending;
        PaymentStatus = paymentStatus;
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

    /// <summary>Cómo llamó la tienda al envío (<c>Envío estándar</c>). Sólo se muestra: no decide nada.</summary>
    public string? ExternalShippingTitle { get; private set; }

    public Guid ClientId { get; private set; }

    public MemberId AdvisorId { get; private set; }

    public string Currency { get; private set; }

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

    /// <summary>Sin tope (plan 2026-10-15, P2): hereda las notas de la cotización, que no lo tienen.</summary>
    public string? Notes { get; private set; }

    public DateTimeOffset ConvertedAt { get; private set; }

    public MemberId ConvertedBy { get; private set; }

    /// <summary>Cuándo se aprobó, y quién. Null mientras el pedido sigue pendiente de revisión.</summary>
    public DateTimeOffset? ApprovedAt { get; private set; }

    public MemberId? ApprovedBy { get; private set; }

    /// <summary>Cuándo se anuló, quién y por qué (spec 2026-09-16). Van aparte de la aprobación, que
    /// se conserva: un pedido aprobado por error y después anulado sigue diciendo quién lo aprobó.</summary>
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
    /// borró el texto precargado).</summary>
    public static Order FromQuotation(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        Quotation quotation,
        OrderPaymentStatus paymentStatus,
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
            quotation.BillingAccount, paymentStatus, notes ?? quotation.Notes, convertedBy, occurredAt);
        // Copia tal cual, incluida la tasa: la cotización ya validó el envío al fijarlo, y el pedido nace con
        // sus mismos totales.
        order.AssignShipping(quotation.ShippingDetails);
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var item in quotation.Items.OrderBy(item => item.Position))
        {
            var copy = OrderItem.Create(
                OrderItemId.New(), order.Id, item.ProductId, item.Quantity, item.UnitPrice, item.DiscountPercentage,
                item.TaxPercentage, order._items.Count + 1, order.Currency, occurredAt);
            copy.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, order.Currency, occurredAt);
            if (item.ProductCode is not null)
            {
                copy.CaptureProduct(new QuotationProductSnapshot(item.ProductCode, item.ProductName ?? string.Empty));
            }

            order._items.Add(copy);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, convertedBy, occurredAt);
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
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        return Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus, notes, createdBy, proofs, occurredAt);
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
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt) =>
        Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus, notes, createdBy, proofs, occurredAt);

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
        if (source is OrderSource.Quotation or OrderSource.Manual)
        {
            throw new QuotationsDomainException(
                "order.order.source_invalid",
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
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId createdBy,
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(shipping);
        var order = new Order(
            id, tenantId, orderNumber, source, null, externalReference, header, parties, billingAccount,
            paymentStatus, notes, createdBy, occurredAt);
        order.AssignShipping(shipping.Validated(order.IsStorePickup, order.Currency, "order.order"));
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var line in lines)
        {
            order.AddItemCore(
                OrderItemId.New(), line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.TaxPercentage, line.DiscountOrigin, line.Product, occurredAt);
        }

        order.EnsureCreatable();
        order.RecalculateTotals();
        order.AddProofs(proofs, createdBy, occurredAt);
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

        if (Billing is { } billing && !billing.IsComplete)
        {
            throw new QuotationsDomainException(
                "order.billing.party_incomplete",
                "The billing party must have all its fields filled.");
        }

        if (Shipping is { } shipping && !shipping.IsComplete)
        {
            throw new QuotationsDomainException(
                "order.shipping.party_incomplete",
                "The shipping party must have all its fields filled.");
        }

        if (Billing is { } named
            && !string.IsNullOrWhiteSpace(named.Name)
            && string.IsNullOrWhiteSpace(named.IdentificationNumber))
        {
            throw new QuotationsDomainException(
                "order.billing.identification_required",
                "A billing party with its own name requires an identification number.");
        }

        if (Billing is not null && (PartyWithRetention is null || PartyVatSurplus is null))
        {
            throw new QuotationsDomainException(
                "order.billing.tax_profile_required",
                "The billing party must state whether it applies withholding tax and VAT surplus.");
        }
    }

    public void AddItem(
        OrderItemId itemId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsurePending("Products can only be added while the order is pending.");
        AddItemCore(
            itemId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            QuotationDiscountOrigin.Own, null, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    private void AddItemCore(
        OrderItemId itemId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        QuotationDiscountOrigin origin,
        QuotationProductSnapshot? product,
        DateTimeOffset occurredAt)
    {
        if (_items.Any(existing => existing.ProductId == productId))
        {
            throw new QuotationsDomainException(
                "order.item.duplicate_product",
                "The product is already in the order.");
        }

        var item = OrderItem.Create(
            itemId, Id, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            _items.Count + 1, Currency, occurredAt);
        if (origin != QuotationDiscountOrigin.Own)
        {
            item.ApplyDiscount(discountPercentage, origin, Currency, occurredAt);
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
        EnsurePending("Products can only be edited while the order is pending.");
        FindItem(itemId).UpdateQuantity(quantity, discountPercentage, taxPercentage, Currency, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(OrderItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("Products can only be removed while the order is pending.");
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
        EnsurePending("The pricing can only change while the order is pending.");
        IsRetail = isRetail;
        if (isRetail)
        {
            GlobalScaleFloor = null;
        }

        Touch(updatedBy, occurredAt);
    }

    public void SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("The pricing can only change while the order is pending.");
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
        EnsurePending("The shipping can only change while the order is pending.");
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
                item.ApplyDiscount(discount.Percentage, discount.Origin, Currency, occurredAt);
            }
        }

        RecalculateTotals();
    }

    public void RefreshCustomerTaxProfile(bool customerWithRetention, bool customerVatSurplus)
    {
        if (Status != OrderStatus.Pending)
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
            if (products.TryGetValue(item.ProductId, out var product))
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

    public void Approve(MemberId approvedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "Only a pending order can be approved.");
        }

        Status = OrderStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)
    {
        // Spec 2026-10-05, decisión 2: la factura ya existe fuera de QEP y anular el pedido no la
        // anula. Va antes que already_cancelled y que el motivo: un facturado nunca se anula, así que
        // pedir el motivo primero mandaría a corregir algo que no destraba nada.
        if (Status == OrderStatus.Invoiced)
        {
            throw new QuotationsDomainException(
                "order.order.already_invoiced",
                "An invoiced order cannot be cancelled; revert the invoicing first.");
        }

        if (Status == OrderStatus.Cancelled)
        {
            throw new QuotationsDomainException(
                "order.order.already_cancelled",
                "The order is already cancelled.");
        }

        var normalizedReason = NormalizeCancellationReason(reason);
        Status = OrderStatus.Cancelled;
        CancelledBy = cancelledBy;
        CancelledAt = occurredAt;
        CancellationReason = normalizedReason;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void Invoice(MemberId invoicedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Approved)
        {
            throw new QuotationsDomainException(
                "order.order.not_approved",
                "Only an approved order can be invoiced.");
        }

        Status = OrderStatus.Invoiced;
        InvoicedBy = invoicedBy;
        InvoicedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Invoiced)
        {
            throw new QuotationsDomainException(
                "order.order.not_invoiced",
                "Only an invoiced order can have its invoicing reverted.");
        }

        Status = OrderStatus.Approved;
        InvoicedBy = null;
        InvoicedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void AddPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        OrderPaymentStatus paymentStatus,
        string? notes,
        MemberId uploadedBy,
        DateTimeOffset occurredAt,
        IReadOnlyCollection<OrderPaymentProofAmountUpdate>? updatedProofs = null)
    {
        updatedProofs ??= [];
        if (Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "Payment proofs can only be added to a pending order.");
        }

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

        PaymentStatus = paymentStatus;
        Notes = NormalizeNotes(notes);
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Compara lo consignado contra el neto a cobrar del propio pedido (spec 2026-10-15 §4.1):
    /// ya no recibe el monto, lo tiene.</summary>
    public void RecalculatePaymentStatus(DateTimeOffset occurredAt)
    {
        EnsurePending("The payment status can only be recalculated on a pending order.");
        var proofsTotal = _paymentProofs.Sum(proof => proof.Amount);
        PaymentStatus = proofsTotal <= 0
            ? OrderPaymentStatus.PaymentPending
            : proofsTotal >= NetTotal
                ? OrderPaymentStatus.FullPaymentReceived
                : OrderPaymentStatus.PartialPaymentReceived;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Spec de Shopify §6.3 (vía spec 2026-10-15 §5.4): el estado de pago que avisa la plataforma,
    /// sólo si el pedido no tiene comprobantes propios; con comprobantes, el estado lo gobiernan ellos.</summary>
    public void SyncExternalPaymentStatus(OrderPaymentStatus status, DateTimeOffset occurredAt)
    {
        EnsurePending("The payment status can only be synced on a pending order.");
        if (_paymentProofs.Count > 0)
        {
            throw new QuotationsDomainException(
                "order.order.payment_has_proofs",
                "The payment status is governed by the attached proofs.");
        }

        if (PaymentStatus == status)
        {
            return;
        }

        PaymentStatus = status;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void RemovePaymentProof(OrderPaymentProofId proofId, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "Payment proofs can only be removed from a pending order.");
        }

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
        EnsurePending("Payment proofs can only be added to a pending order.");
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
        EnsurePending("Payment proofs can only be corrected on a pending order.");
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
        EnsurePending("The notes can only be edited on a pending order.");
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

    // US-14: "se requiere al menos un comprobante, salvo que el estado del pago sea
    // 'Payment pending'". Sólo para los pedidos que se cobran desde QEP: uno de origen externo entra
    // con el estado de pago que avisa la plataforma y el pago vive allá (spec de Shopify §6.3).
    private void AddProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)
    {
        if (proofs.Count == 0 && PaymentStatus != OrderPaymentStatus.PaymentPending && !IsExternal)
        {
            throw new QuotationsDomainException(
                "order.order.payment_proof_required",
                "At least one payment proof is required unless the payment status is pending.");
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(OrderPaymentProof.Create(
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt, proof.PaidOn));
        }
    }

    private bool IsExternal => Source is not OrderSource.Quotation and not OrderSource.Manual;

    private void EnsurePending(string message)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException("order.order.not_pending", message);
        }
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
            FreightAmounts.For(FreightChargeMode, FreightAmount, FreightTaxPercentage, Currency),
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

    private static void EnsureBillingIsConsistent(QuotationParties parties)
    {
        if (parties.BillsToFinalConsumer && (parties.Billing is not null || parties.BillingUsesBusinessName))
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
