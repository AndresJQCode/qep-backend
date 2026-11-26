using BuildingBlocks.Domain.Catalog;
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
    private readonly List<DocumentWithholding> _withholdings = [];

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
        DateOnly orderDate,
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
        CustomerVatSurplus = header.CustomerVatSurplus;
        ArgumentNullException.ThrowIfNull(header.Withholdings);
        ReplaceWithholdings(header.Withholdings);
        EnsureBillingIsConsistent(parties.BillsToFinalConsumer, parties.Billing is not null, parties.BillingUsesBusinessName);
        AssignParties(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        IsStorePickup = parties.IsStorePickup;
        BillsToFinalConsumer = parties.BillsToFinalConsumer;
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        BillingAccount = billingAccount?.Normalized();
        // La misma regla que Quotation: la cuenta fija la moneda y, sin ella, manda la del encabezado. Sin cuenta
        // bancaria (empresa sola, spec 2026-11-17 R3) la moneda la decide quien llama: la por defecto del tenant en un
        // pedido manual, la de la cotización al convertir, la de la tienda o la del pedido de origen.
        Currency = BillingAccount is { HasBankAccount: true } account ? account.Currency : header.Currency;
        Status = OrderStatus.Open;
        FulfillmentStatus = OrderFulfillmentStatus.NotPrepared;
        // Null = lo calculan los comprobantes, que se agregan después (ver CalculatedPaymentStatus).
        PaymentStatus = paymentStatus ?? OrderPaymentStatus.PaymentPending;
        Notes = NormalizeNotes(notes);
        ConvertedAt = occurredAt;
        ConvertedBy = createdBy;
        OrderDate = orderDate;
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

    /// <summary>El tipo de documento (spec 2026-11-26 §6.2): el de la cotización de la que nació. Un pedido manual o de una
    /// integración es <see cref="QuotationKind.Standard"/>.</summary>
    public QuotationKind Kind { get; private set; }

    /// <summary>El alcance vendido, copiado de la cotización con los mismos ids; <c>null</c> en un pedido
    /// <see cref="QuotationKind.Standard"/>. No se edita (P4).</summary>
    public ProjectScope? ProjectScope { get; private set; }

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

    /// <summary>Spec 2026-11-15 §3.3: <c>true</c> cuando un asesor quitó o cambió la cuenta del pedido. Desde ahí ningún
    /// enlace automático (checkout, inicio de sesión ni alta diferida) vuelve a tocarla: si no, quitar la cuenta de un
    /// pedido con el correo mal escrito no serviría, porque el dueño de ese correo lo recuperaría al iniciar sesión.</summary>
    public bool BuyerAccountSetByMember { get; private set; }

    /// <summary>Mayorista fase 3: el tenant distribuidor cuya orden de compra creó el pedido. Null fuera de
    /// <see cref="OrderSource.Wholesale"/>.</summary>
    public Guid? BuyerTenantId { get; private set; }

    /// <summary>La orden de compra (en el tenant del distribuidor) que creó el pedido. Null fuera de
    /// <see cref="OrderSource.Wholesale"/>.</summary>
    public Guid? PurchaseOrderId { get; private set; }

    /// <summary>D-1: «Por aprobar» es una marca, no un estado. Cuándo y quién aprobó el pedido mayorista; desde ahí deja
    /// de ser editable (D-3) y la orden del distribuidor queda confirmada con estas líneas.</summary>
    public DateTimeOffset? WholesaleApprovedAt { get; private set; }

    public MemberId? WholesaleApprovedBy { get; private set; }

    /// <summary>Un pedido mayorista abierto que nadie aprobó todavía.</summary>
    public bool IsAwaitingWholesaleApproval =>
        Source == OrderSource.Wholesale && Status == OrderStatus.Open && WholesaleApprovedAt is null;

    /// <summary>Lo anuló el comprador (la tienda o el distribuidor): es la única anulación que no deja
    /// <see cref="CancelledBy"/>, porque no hay un miembro del tenant detrás.</summary>
    public bool CancelledByBuyer => Status == OrderStatus.Cancelled && CancelledAt is not null && CancelledBy is null;

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
    /// <see cref="FromQuotation"/>, <see cref="CreateManual"/> y <see cref="CreateExternal"/> la exigen. Desde el spec
    /// 2026-11-17 (R3) puede ser sólo la empresa, con banco y número nulos.</summary>
    public QuotationBillingAccount? BillingAccount { get; private set; }

    /// <summary>
    /// Si lo que emite este pedido lleva IVA (spec 2026-11-17 §5.2): el de la empresa congelado con la cuenta. No se
    /// relee de la empresa: un pedido emitido no cambia de IVA porque la empresa cambie después.
    /// </summary>
    public bool ChargesVat => BillingAccount?.ChargesVat ?? true;

    /// <summary>
    /// Si aplicar esa cuenta (<see cref="ApplyBillingContext"/>) vuelve a gravar el envío (spec 2026-11-17, R2): hoy sin
    /// IVA, con IVA después, con un envío cobrado con valor en COP. Una cuenta null conserva la que hay, y la misma empresa
    /// conserva su IVA congelado, así que ninguna de las dos cambia nada. Si dice que sí, el handler lee la tasa del envío del tenant (puede faltar:
    /// <c>order.order.freight_tax_rate_missing</c>) y la pasa.
    /// </summary>
    public bool NeedsFreightTaxRateFor(QuotationBillingAccount? billingAccount) =>
        billingAccount is not null
        && billingAccount.CompanyId != BillingAccount?.CompanyId
        && !ChargesVat
        && billingAccount.ChargesVat
        && FreightChargeMode == Domain.FreightChargeMode.Charged
        && FreightAmount > 0
        && QuotationItem.ItemizesTaxIn(Currency);

    private void ApplyChargesVat(int? freightTaxPercentageWithVat, DateTimeOffset occurredAt)
    {
        foreach (var item in _items)
        {
            item.ApplyChargesVat(ChargesVat, Currency, PriceRounding, occurredAt);
        }

        if (!ChargesVat)
        {
            FreightTaxPercentage = 0;
        }
        else if (FreightChargeMode == Domain.FreightChargeMode.Charged
            && FreightAmount > 0
            && QuotationItem.ItemizesTaxIn(Currency))
        {
            FreightTaxPercentage = freightTaxPercentageWithVat
                ?? throw new InvalidOperationException(
                    "Turning VAT on with a charged freight needs the tenant freight tax rate (NeedsFreightTaxRateFor).");
        }
    }

    /// <summary>
    /// El municipio de la operación (spec ReteICA por empresa §7.5), el mismo concepto que en la cotización: decide qué
    /// tasa municipal aplica. Null en un país sin tipos municipales y en un pedido anterior a este trabajo (A8).
    /// </summary>
    public Guid? OperationCityId { get; private set; }

    public string? PaymentMethod { get; private set; }

    public bool IsRetail { get; private set; }

    public int? GlobalScaleFloor { get; private set; }

    public bool BillingUsesBusinessName { get; private set; }

    public bool IsStorePickup { get; private set; }

    public bool BillsToFinalConsumer { get; private set; }

    public bool? PartyVatSurplus { get; private set; }

    /// <summary>El excedente de IVA del cliente, congelado al crear (spec impuestos por tipo D24): editar la ficha
    /// del cliente después no lo mueve.</summary>
    public bool CustomerVatSurplus { get; private set; }

    /// <summary>
    /// La foto de las tasas de retención del pedido (spec impuestos por tipo §7.5). Una sola fuente,
    /// <see cref="WithholdingSource.Document"/>: al convertir hereda la fuente vigente de la cotización, al crear a mano
    /// o desde una integración trae las del cliente, y mientras es <see cref="IsEditable"/> la reemplazan los
    /// selectores (<see cref="SetWithholdings"/>, D21). Se guardan también las que no aplican.
    /// </summary>
    public IReadOnlyCollection<DocumentWithholding> Withholdings => _withholdings;

    /// <summary>Las que aplicaron en el último recálculo, en su orden; su suma es <see cref="RetentionAmount"/>.</summary>
    public IEnumerable<DocumentWithholding> AppliedWithholdings =>
        _withholdings.Where(withholding => withholding.Applied).OrderBy(withholding => withholding.Position);

    /// <summary>Por qué cada tasa del pedido aplica o no, en su orden (spec retenciones por UVT §5.1); las de otro país
    /// no salen.</summary>
    public IReadOnlyList<WithholdingVerdict> WithholdingVerdicts =>
        DocumentWithholding.Explain(
            [.. _withholdings.OrderBy(withholding => withholding.Position)], BillsToFinalConsumer, BillingAccount, Currency,
            OperationCityId, WithholdingBases.From(_items.Select(item => item.Amounts)));

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
    /// congela, como en Shopify. Un pedido mayorista aprobado tampoco (D-3): la aprobación ya le llevó sus líneas al
    /// distribuidor y una edición posterior nunca le llegaría.</summary>
    public bool IsEditable =>
        Status == OrderStatus.Open
        && !IsPrepared
        && !IsInvoiced
        && !(Source == OrderSource.Wholesale && WholesaleApprovedAt is not null);

    /// <summary>Sin tope (plan 2026-10-15, P2): hereda las notas de la cotización, que no lo tienen.</summary>
    public string? Notes { get; private set; }

    /// <summary>
    /// La nota que el comprador escribió en el checkout (spec 2026-11-10, D9; Shopify desde el spec 2026-11-14).
    /// <see cref="Notes"/> la sobrescriben <c>UpdateNotes</c>, <c>AddPaymentProofs</c> y <c>AttachExternalShipping</c>;
    /// ésta la escriben sólo <see cref="CreateFromStore"/>, <see cref="CreateExternal"/> (la importación de Shopify) y
    /// <see cref="UpdateExternalBuyerNotes"/> (la sincronización de Shopify y la corrección del asesor, sólo en pedidos de
    /// Shopify). En un pedido de la tienda pública no cambia, para que el comprador siga viendo lo que escribió. Los
    /// pedidos de Shopify importados antes del spec 2026-11-14 la tienen en <c>null</c>: su nota sigue dentro de
    /// <see cref="Notes"/> (sin backfill).
    /// </summary>
    public string? BuyerNotes { get; private set; }

    public DateTimeOffset ConvertedAt { get; private set; }

    /// <summary>
    /// «Fecha del pedido» (spec 2026-11-16, F-1 y F-2): el día del negocio en el calendario del tenant. Nace con el día
    /// local de la creación, que Application resuelve porque el dominio no conoce el huso, y se corrige con
    /// <see cref="ChangeOrderDate"/>. Va aparte de <see cref="ConvertedAt"/>, que es el registro técnico de cuándo nació y
    /// no cambia nunca.
    /// </summary>
    public DateOnly OrderDate { get; private set; }

    public MemberId ConvertedBy { get; private set; }

    /// <summary>Cuándo se marcó como pagado, y quién (spec 2026-11-10 §3.2). Null si nadie lo marcó: un pago de la
    /// plataforma no lo llena.</summary>
    public DateTimeOffset? PaidMarkedAt { get; private set; }

    public MemberId? PaidMarkedBy { get; private set; }

    /// <summary>El momento en que el pedido quedó pagado la última vez, por cualquier vía (spec costo de producto
    /// 2026-11-10 §4.4): «Marcar como pagado», la plataforma o nacer pagado. Se limpia al salir de pagado. Es la
    /// fecha del reporte de margen; <see cref="PaidMarkedAt"/> no sirve porque la plataforma no la llena.</summary>
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Las variantes de las líneas que todavía no congelaron su costo, sin repetir: lo que Application
    /// tiene que buscar antes de llevar el pedido a pagado.</summary>
    public IReadOnlyList<Guid> UncostedVariantIds =>
        _items.Where(item => item.CostCapturedAt is null).Select(item => item.VariantId).Distinct().ToArray();

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

    public const int InvoiceProviderMaxLength = 16;

    public const int InvoiceNumberMaxLength = 40;

    public const int InvoiceExternalIdMaxLength = 64;

    /// <summary>Quién emitió la factura: <c>siigo</c> si la creó una integración (spec 2026-11-18 §5.5), <c>null</c> si la marca es
    /// manual (alguien la hizo afuera y sólo dejó constancia). La marca de un proveedor no se revierte con <see cref="RevertInvoicing"/>:
    /// sólo se quita cuando su factura se anuló allá (<see cref="RevertExternalInvoicing"/>).</summary>
    public string? InvoiceProvider { get; private set; }

    /// <summary>El número que el proveedor le dio a la factura (<c>FV-1-123</c> en Siigo). <c>null</c> en una marca manual.</summary>
    public string? InvoiceNumber { get; private set; }

    /// <summary>El id de la factura en el sistema del proveedor. Nunca viaja en la respuesta de la API.</summary>
    public string? InvoiceExternalId { get; private set; }

    /// <summary>Vacío hasta que se sincronice — placeholder para la integración futura con
    /// Ritual Collection. Esta fase no la implementa.</summary>
    public string? RitualCollectionSyncId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyCollection<OrderPaymentProof> PaymentProofs => _paymentProofs;

    public IReadOnlyCollection<OrderItem> Items => _items;

    /// <summary>
    /// Si el pedido vende algo que se despacha (spec 2026-11-18 §5), con la misma regla que
    /// <see cref="Quotation.RequiresShipping"/>. Con false la pantalla oculta el envío y dice «Marcar como prestado»
    /// en vez de «Preparar»; el dominio no cambia nada: <c>Prepare</c> y el resto siguen igual.
    /// </summary>
    public bool RequiresShipping =>
        Kind != QuotationKind.Project
        && (_items.Count == 0 || _items.Exists(item => item.Kind == ProductKind.Physical));

    public IReadOnlyCollection<OrderParty> Parties => _parties;

    public OrderParty? Billing => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Billing);

    public OrderParty? Shipping => _parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping);

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
        DateTimeOffset occurredAt,
        DateOnly orderDate,
        IReadOnlyDictionary<string, TaxUnitPhoto>? taxUnits = null)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        if (quotation.BillingAccount is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing company before it can be converted to an order.");
        }

        var header = new OrderHeader(
            quotation.ClientId,
            quotation.AdvisorId,
            quotation.Currency,
            quotation.PaymentMethod,
            quotation.IsRetail,
            quotation.GlobalScaleFloor,
            quotation.CustomerVatSurplus,
            // Hereda la fuente vigente de la cotización tal cual (spec §7.5); la otra no viaja: el pedido tiene una sola.
            [.. quotation.ActiveWithholdings.Select(withholding => withholding.Rate)]);
        var parties = new QuotationParties(
            quotation.Billing?.ToDetails(),
            quotation.Shipping?.ToDetails(),
            quotation.BillingUsesBusinessName,
            quotation.IsStorePickup,
            quotation.BillsToFinalConsumer,
            BillingVatSurplus: quotation.PartyVatSurplus);
        var order = new Order(
            id, tenantId, orderNumber, OrderSource.Quotation, quotation.Id, null, header, parties,
            quotation.BillingAccount, paymentStatus: null, notes ?? quotation.Notes, convertedBy, occurredAt, orderDate);
        // Spec ReteICA por empresa §7.5: hereda el municipio de la operación junto con la foto, y las filas con su
        // porcentaje efectivo y el del catálogo (los lleva Rate).
        order.OperationCityId = quotation.OperationCityId;
        // El tipo y el alcance vendido, con los mismos ids (spec 2026-11-26 §6.6). La línea se copia abajo como cualquiera.
        order.Kind = quotation.Kind;
        order.ProjectScope = quotation.ProjectScope;
        // Copia tal cual, incluida la tasa: la cotización ya validó el envío al fijarlo, y el pedido nace con
        // sus mismos totales.
        order.AssignShipping(quotation.ShippingDetails);
        // La regla de la cotización, antes de copiar las líneas: el pedido tiene los mismos centavos.
        order._priceRoundingDecimals = quotation.PriceRounding.Decimals;
        order._priceRoundingMode = quotation.PriceRounding.Mode;
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var item in quotation.Items.OrderBy(item => item.Position))
        {
            // La tasa del producto y la cuenta (con su IVA) se copian tal cual: no se relee la empresa (R2). El tipo y la
            // unidad, también: son los de la línea de la cotización, no los que el producto tenga hoy (spec 2026-11-18 §3.4).
            var copy = OrderItem.Create(
                OrderItemId.New(), order.Id, item.VariantId, item.ProductId, item.Quantity, item.UnitPrice, item.DiscountPercentage,
                item.ProductTaxRate, order.ChargesVat, order._items.Count + 1, order.Currency, order.PriceRounding,
                item.Kind, item.SalesUnit, occurredAt);
            copy.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, order.Currency, order.PriceRounding, occurredAt);
            if (item.ProductCode is not null)
            {
                copy.CaptureProduct(new QuotationProductSnapshot(item.ProductCode, item.ProductName ?? string.Empty));
            }

            order._items.Add(copy);
        }

        // Spec alerta UVT §5.2: el pedido nace con la UVT de su año, no con la foto congelada de la cotización. Va
        // antes de los totales y del estado de pago, que se comparan contra el neto ya refrescado. No pasa por
        // RefreshTaxUnits del pedido: exige IsEditable y recalcularía otra vez. Null deja la foto como viene.
        if (taxUnits is not null)
        {
            DocumentWithholding.RefreshTaxUnits(order._withholdings, taxUnits);
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
        DateTimeOffset occurredAt,
        DateOnly orderDate)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        return Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus: null, notes, createdBy, proofs, occurredAt, orderDate, OrderLineCosts.None);
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
        DateTimeOffset occurredAt,
        DateOnly orderDate) =>
        Build(
            id, tenantId, orderNumber, OrderSource.Manual, null, header, parties, billingAccount, shipping, lines,
            paymentStatus: null, notes, createdBy, proofs, occurredAt, orderDate, OrderLineCosts.None);

    /// <summary>Un pedido que trae una integración (Shopify y las que vengan). La elegibilidad del cliente
    /// —completo o incompleto— vive en Application: el dominio sólo exige que haya un id. Si nace
    /// <see cref="OrderPaymentStatus.FullPaymentReceived"/>, congela el costo de sus líneas con
    /// <paramref name="costs"/> al crearse (spec costo de producto §4.3). <paramref name="buyerNotes"/> es la nota del
    /// comprador en la plataforma, que va a <see cref="BuyerNotes"/> y no a <see cref="Notes"/> (spec 2026-11-14).</summary>
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
        DateOnly orderDate,
        OrderSource source,
        string externalReference,
        ShippingDetails shipping,
        string? externalKey,
        string? externalShippingTitle,
        OrderLineCosts costs,
        string? buyerNotes = null)
    {
        if (source is OrderSource.Quotation or OrderSource.Manual or OrderSource.Store or OrderSource.Wholesale)
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
            shipping, lines, paymentStatus, notes, createdBy, proofs, occurredAt, orderDate, costs);
        order.ExternalKey = key;
        order.ExternalShippingTitle = NormalizeExternalShippingTitle(externalShippingTitle);
        order.BuyerNotes = NormalizeNotes(buyerNotes);
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
        DateOnly orderDate,
        string externalKey,
        Guid? buyerAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        var key = NormalizeExternalKey(externalKey);
        var order = Build(
            id, tenantId, orderNumber, OrderSource.Store, externalReference: null, header, parties, billingAccount,
            ShippingDetails.None, lines, OrderPaymentStatus.PaymentPending, notes, createdBy, [], occurredAt, orderDate,
            OrderLineCosts.None);
        order.ExternalKey = key;
        order.BuyerAccountId = buyerAccountId;
        order.BuyerNotes = NormalizeNotes(notes);
        return order;
    }

    /// <summary>La clave de idempotencia del envío de una orden de compra vinculada (D-8): un reintento encuentra el pedido
    /// que ya existe en vez de crear otro. Lleva al comprador (rulings X1 y X8a): el id de la orden de otro comprador da otra
    /// clave y nunca encuentra este pedido.</summary>
    public static string WholesaleExternalKey(Guid buyerTenantId, Guid purchaseOrderId) =>
        $"purchase-order:{buyerTenantId:D}:{purchaseOrderId:D}";

    /// <summary>
    /// El pedido que crea la orden de compra de un distribuidor vinculado (mayorista fase 3). Calca
    /// <see cref="CreateFromStore"/>: pago pendiente, sin comprobantes ni envío, la nota del comprador en
    /// <see cref="BuyerNotes"/>. La referencia externa es el número de la orden (D-7) y cada línea guarda la línea de la
    /// orden de la que salió. Nace por aprobar (D-1).
    /// </summary>
    public static Order CreateFromWholesale(
        OrderId id,
        Guid tenantId,
        string orderNumber,
        OrderHeader header,
        QuotationParties parties,
        QuotationBillingAccount billingAccount,
        IReadOnlyCollection<OrderLineInput> lines,
        string? buyerNotes,
        MemberId createdBy,
        DateTimeOffset occurredAt,
        DateOnly orderDate,
        Guid buyerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber)
    {
        ArgumentNullException.ThrowIfNull(billingAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseOrderNumber);
        var order = Build(
            id, tenantId, orderNumber, OrderSource.Wholesale, purchaseOrderNumber, header, parties, billingAccount,
            ShippingDetails.None, lines, OrderPaymentStatus.PaymentPending, buyerNotes, createdBy, [], occurredAt,
            orderDate, OrderLineCosts.None);
        order.ExternalKey = NormalizeExternalKey(WholesaleExternalKey(buyerTenantId, purchaseOrderId));
        order.BuyerNotes = NormalizeNotes(buyerNotes);
        order.BuyerTenantId = buyerTenantId;
        order.PurchaseOrderId = purchaseOrderId;
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
        DateTimeOffset occurredAt,
        DateOnly orderDate)
    {
        var nobody = new MemberId(Guid.Empty);
        var header = new OrderHeader(Guid.Empty, nobody, currency, null, isRetail, null, false, []);
        return Build(
            OrderId.New(), tenantId, "STORE-QUOTE", OrderSource.Store, null, header, QuotationParties.Empty, null,
            ShippingDetails.None, lines, OrderPaymentStatus.PaymentPending, null, nobody, [], occurredAt, orderDate,
            OrderLineCosts.None, allowNoClient: true);
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
        DateOnly orderDate,
        OrderLineCosts costs,
        bool allowNoClient = false)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(shipping);
        ArgumentNullException.ThrowIfNull(costs);
        var order = new Order(
            id, tenantId, orderNumber, source, null, externalReference, header, parties, billingAccount,
            paymentStatus, notes, createdBy, occurredAt, orderDate, allowNoClient);
        order.AssignShipping(shipping.Validated(order.IsStorePickup, order.Currency, "order.order"));
        order.EnsureCashOnDeliveryIsPending(proofs);
        foreach (var line in lines)
        {
            var item = order.AddItemCore(
                OrderItemId.New(), line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.TaxPercentage, line.DiscountOrigin, line.Product, line.Kind, line.SalesUnit, occurredAt,
                isGift: line.IsGift);
            item.LinkPurchaseOrderLine(line.SourcePurchaseOrderLineId);
        }

        order.EnsureCreatable();
        // Spec ReteICA por empresa §7.5 (R5, A10): un pedido que nace sin cotización arranca en el municipio de la
        // empresa. Las filas municipales del encabezado ya vienen resueltas para ese municipio.
        order.OperationCityId = OperationCityChoice.Company.ForNewCompany(order.BillingAccount);
        order.RecalculateTotals();
        order.AddProofs(proofs, createdBy, occurredAt);
        if (paymentStatus is null)
        {
            order.PaymentStatus = order.CalculatedPaymentStatus();
        }
        else if (paymentStatus == OrderPaymentStatus.FullPaymentReceived)
        {
            // Nace vendido: la regla única (§4.3) congela al crearse, no en un pago que nunca va a llegar.
            order.FreezeLineCosts(costs, occurredAt);
            order.PaidAt = occurredAt;
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

        EnsureBillingRules(Billing, PartyVatSurplus);

        if (Shipping is { } shipping && !shipping.IsComplete)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyIncomplete,
                "The shipping party must have all its fields filled.");
        }
    }

    // Las reglas de facturación del alta, compartidas con SetBilling para que editar no pueda dejar un
    // pedido que crear habría rechazado (spec 2026-10-26).
    private static void EnsureBillingRules(OrderParty? billing, bool? partyVatSurplus)
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

        // Sólo el excedente de IVA se pregunta (spec impuestos por tipo §7.8): las retenciones son selectores, y
        // ninguno elegido es una respuesta válida.
        if (partyVatSurplus is null)
        {
            throw new QuotationsDomainException(
                "order.billing.tax_profile_required",
                "The billing party must state whether it has VAT surplus.");
        }
    }

    /// <summary>
    /// Cambia a quién se le factura en un pedido pendiente (spec 2026-10-26): la fila de facturación, el
    /// consumidor final, la razón social y el excedente de IVA de la fila. La fila existente se <b>modifica</b>
    /// y no se reemplaza: borrar y re-crear en el mismo guardado choca con <c>IX_order_parties_order_role</c>.
    /// Devuelve <c>false</c>, sin tocar la versión, si nada cambió. Con <paramref name="enforceCompleteness"/> en
    /// <c>false</c> (el cálculo previo, que se hace mientras la persona teclea) se omiten las exigencias de fila
    /// completa, identificación y excedente de IVA; la consistencia con el consumidor final sigue. Las retenciones
    /// no cambian acá: el pedido tiene una sola fuente y la cambia <see cref="SetWithholdings"/>.
    /// </summary>
    public bool SetBilling(
        QuotationPartyDetails? billing,
        bool billsToFinalConsumer,
        bool billingUsesBusinessName,
        bool? partyVatSurplus,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        bool enforceCompleteness = true)
    {
        EnsureEditable("The billing can only change while the order is editable.");
        EnsureBillingIsConsistent(billsToFinalConsumer, billing is not null, billingUsesBusinessName);

        // La fila candidata se arma aparte para validar lo normalizado antes de mutar nada.
        var candidate = billing is null ? null : OrderParty.Create(Id, QuotationPartyRole.Billing, billing);
        var vatSurplus = billing is null ? null : partyVatSurplus;
        if (enforceCompleteness)
        {
            EnsureBillingRules(candidate, vatSurplus);
        }

        var current = Billing;
        if (current?.ToDetails() == candidate?.ToDetails()
            && (current is null) == (candidate is null)
            && BillsToFinalConsumer == billsToFinalConsumer
            && BillingUsesBusinessName == billingUsesBusinessName
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
        PartyVatSurplus = vatSurplus;
        Touch(updatedBy, occurredAt);
        return true;
    }

    /// <summary>
    /// Los tres motivos por los que la entrega no se puede cambiar, del más específico al general (spec 2026-11-24 §10.1):
    /// facturado, preparado y no editable. El guardado y el cálculo previo lo llaman antes de su guarda general, para que
    /// la pantalla reciba el motivo exacto y no <c>order.order.not_editable</c>.
    /// </summary>
    public void EnsureDeliveryEditable()
    {
        EnsureNotInvoiced("The delivery of an invoiced order cannot change.");
        EnsureNotPrepared("The delivery of a prepared order cannot change.");
        EnsureEditable("The delivery can only change while the order is editable.");
    }

    /// <summary>
    /// Cambia recoger o enviar y a dónde (owner 2026-11-24: revierte «el pedido no edita sus partes» para la entrega), en
    /// todo origen. <paramref name="shipping"/> null con envío = los datos del cliente, sin fila. A recoger quita la fila y
    /// limpia el envío, contraentrega incluida: no hay a dónde mandar nada. La fila existente se <b>modifica</b> y no se
    /// reemplaza (<c>IX_order_parties_order_role</c>, como <see cref="SetBilling"/>). Devuelve <c>false</c>, sin tocar la
    /// versión, si nada cambió. Una parte propia debe venir completa, como en el alta
    /// (<c>order.shipping.party_incomplete</c>); con <paramref name="enforceCompleteness"/> en <c>false</c> (el cálculo
    /// previo, que se hace mientras la persona teclea) no se exige.
    /// </summary>
    public bool SetDelivery(
        bool isStorePickup,
        QuotationPartyDetails? shipping,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        bool enforceCompleteness = true)
    {
        EnsureDeliveryEditable();
        if (isStorePickup && shipping is not null)
        {
            throw new QuotationsDomainException(
                OrderDeliveryErrors.ShippingPartyOnPickup, "A store pickup cannot carry a shipping party.");
        }

        // La fila candidata se arma aparte para validar y comparar lo normalizado antes de mutar nada.
        var candidate = shipping is null
            ? null
            : OrderParty.Create(Id, QuotationPartyRole.Shipping, shipping, "Delivery.Shipping");
        if (enforceCompleteness && candidate is { IsComplete: false })
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyIncomplete, "The shipping party must have all its fields filled.");
        }

        var current = Shipping;
        if (IsStorePickup == isStorePickup && current?.ToDetails() == candidate?.ToDetails())
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

        IsStorePickup = isStorePickup;
        if (isStorePickup)
        {
            AssignShipping(ShippingDetails.None);
        }

        Touch(updatedBy, occurredAt);
        return true;
    }

    /// <summary>
    /// Reemplaza las tasas de retención de un pedido <see cref="IsEditable"/> por las de los selectores
    /// (spec impuestos por tipo D21): una por tipo, ya resueltas contra el catálogo por la aplicación. Cambia sólo
    /// este pedido, nunca la ficha del cliente. El agregado vuelve a exigir lo que guarda: empresa facturadora, cada
    /// tasa de su país y una por tipo. Una lista vacía siempre se acepta y deja el pedido sin retenciones.
    ///
    /// Devuelve si cambió algo; si no, no toca la versión.
    /// </summary>
    public bool SetWithholdings(
        IReadOnlyList<WithholdingRate> rates,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(rates);
        EnsureEditable("The withholdings can only change while the order is editable.");
        if (rates.Count > 0)
        {
            EnsureSelectableWithholdings(rates);
        }

        if (_withholdings.OrderBy(withholding => withholding.Position).Select(withholding => withholding.Rate)
            .SequenceEqual(rates))
        {
            return false;
        }

        ReplaceWithholdings(rates);
        Touch(updatedBy, occurredAt);
        return true;
    }

    // Sin empresa no hay país contra el cual validarlos (D22: fijar la empresa habilita los selectores).
    private void EnsureSelectableWithholdings(IReadOnlyList<WithholdingRate> rates) =>
        WithholdingSelectorRules.Ensure(rates, BillingAccount?.Country, OperationCityId, "order.billing");

    /// <summary>
    /// El municipio de la operación que quedaría con esa cuenta y esa elección: la misma regla que
    /// <see cref="Quotation.OperationCityFor"/>. Sin cuenta en el cuerpo se conserva la del pedido.
    /// </summary>
    public Guid? OperationCityFor(QuotationBillingAccount? billingAccount, OperationCityChoice choice) =>
        choice.For(billingAccount ?? BillingAccount, BillingAccount?.CompanyId, OperationCityId);

    /// <summary>
    /// Fija o cambia la empresa y la cuenta con la que se cobra un pedido <see cref="IsEditable"/> (spec impuestos por
    /// tipo D22) y el municipio de la operación (spec ReteICA por empresa §7.5). Con la empresa llega el país, y con el
    /// país las retenciones que aplican; una tasa de otro país deja de aplicar (D23). La aplicación ya validó la cuenta
    /// contra la empresa y armó <paramref name="municipal"/> con <see cref="OperationCityFor"/> (plan P4).
    /// <paramref name="billingAccount"/> null conserva la cuenta.
    ///
    /// No cambia la moneda (plan P10, aprobado por el owner): cambiarla sin repreciar dejaría los importes rotulados
    /// en otra moneda, y repreciar un pedido externo cambiaría lo que la plataforma cobró. Una cuenta en otra moneda
    /// es <c>order.billing.currency_change_not_allowed</c>.
    ///
    /// Otra empresa u otro municipio re-resuelven las filas municipales con la regla del cliente (§7.6): una tasa
    /// municipal donde el cliente no es agente se quita y vuelve en <see cref="MunicipalReResolution.RemovedNotAgent"/>.
    /// Reenviar la misma cuenta con el municipio de la empresa que antes no tenía lo completa sin contar como cambio.
    /// Si nada cambió, no toca la versión.
    /// </summary>
    public OrderBillingContextChange ApplyBillingContext(
        QuotationBillingAccount? billingAccount,
        MunicipalContext municipal,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        int? freightTaxPercentageWithVat = null)
    {
        ArgumentNullException.ThrowIfNull(municipal);
        EnsureEditable("The billing account and the operation city can only change while the order is editable.");
        var previousCompanyId = BillingAccount?.CompanyId;
        var chargedVat = ChargesVat;
        var accountChanged = false;
        if (billingAccount is not null)
        {
            var normalized = billingAccount.Normalized();
            // El IVA es la foto de la empresa al elegirla (spec 2026-11-17 §5.2): reenviar la misma empresa no lo relee,
            // aunque la empresa lo haya cambiado después. Sólo otra empresa trae el suyo.
            if (BillingAccount is { } current && current.CompanyId == normalized.CompanyId)
            {
                normalized = normalized with { ChargesVat = current.ChargesVat };
            }

            // El municipio de la empresa no cuenta: una cuenta anterior a este trabajo lo completa en silencio.
            accountChanged = normalized with { CityId = BillingAccount?.CityId } != BillingAccount;
            if (accountChanged
                && !string.Equals(normalized.Currency, Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new QuotationsDomainException(
                    "order.billing.currency_change_not_allowed",
                    "The billing account must use the order's currency.");
            }

            BillingAccount = normalized;
        }

        // R2: otra empresa con otro IVA rehace las líneas y el envío. Touch, más abajo, rehace los totales.
        if (chargedVat != ChargesVat)
        {
            ApplyChargesVat(freightTaxPercentageWithVat, occurredAt);
        }

        var cityAfter = MunicipalReResolver.HasMunicipalTypes(BillingAccount?.Country) ? municipal.OperationCityId : null;
        if (BillingAccount?.CompanyId == previousCompanyId && cityAfter == OperationCityId)
        {
            if (accountChanged)
            {
                Touch(updatedBy, occurredAt);
            }

            return new OrderBillingContextChange(accountChanged, MunicipalReResolution.None);
        }

        var cityBefore = OperationCityId;
        OperationCityId = cityAfter;
        var resolved = MunicipalReResolver.ReResolve(
            [.. _withholdings.OrderBy(row => row.Position).Select(row => row.Rate)],
            municipal,
            cityAfter,
            customerRule: true);
        ReplaceWithholdings(resolved.Rates);
        Touch(updatedBy, occurredAt);
        return new OrderBillingContextChange(
            accountChanged,
            new MunicipalReResolution(true, cityBefore, cityAfter, resolved.Discarded, resolved.RemovedNotAgent));
    }

    /// <summary>
    /// Cambia el porcentaje efectivo de la retención municipal de <paramref name="type"/> (spec ReteICA por empresa §7.7)
    /// mientras el pedido es <see cref="IsEditable"/>; <c>null</c> vuelve al del catálogo. Devuelve el cambio para el
    /// historial, o null si ya era ese.
    /// </summary>
    public WithholdingPercentageChange? SetWithholdingPercentage(
        string type,
        decimal? percentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable("The withholding percentage can only change while the order is editable.");
        var row = _withholdings.FirstOrDefault(candidate => string.Equals(candidate.Type, type, StringComparison.Ordinal));
        var target = MunicipalReResolver.EnsureEditablePercentage(row, percentage, "order.billing");
        if (target == row!.Percentage)
        {
            return null;
        }

        var change = new WithholdingPercentageChange(row.Name, row.Percentage, target, target == row.CatalogPercentage);
        row.SetPercentage(target);
        Touch(updatedBy, occurredAt);
        return change;
    }

    /// <summary>
    /// Pone en cada tasa con base mínima en unidades la UVT vigente de su país y recalcula, mientras el pedido es
    /// <see cref="IsEditable"/> (spec retenciones por UVT §5.4, owner P1). La clave es el país canónico. No es una
    /// edición del asesor: no sube la versión ni cambia el autor; el handler que guarda o previsualiza ya lo hace.
    /// </summary>
    public void RefreshTaxUnits(IReadOnlyDictionary<string, TaxUnitPhoto> byCountry)
    {
        EnsureEditable("The tax unit can only be refreshed while the order is editable.");
        DocumentWithholding.RefreshTaxUnits(_withholdings, byCountry);
        RecalculateTotals();
    }

    /// <summary>
    /// «Sin retención» (owner 2026-11-13, plan P12): quita la fila de cada tipo marcado mientras el pedido es
    /// <see cref="IsEditable"/>. Sin fila no hace nada. Que los tipos sean municipales lo valida la aplicación. Devuelve las
    /// filas quitadas que tenían porcentaje editado, para el historial.
    /// </summary>
    public IReadOnlyList<WithholdingRate> ClearMunicipalWithholdings(
        IReadOnlyCollection<string> types,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(types);
        EnsureEditable("The withholdings can only change while the order is editable.");
        var current = _withholdings.OrderBy(row => row.Position).Select(row => row.Rate).ToList();
        var removed = current.Where(rate => types.Contains(rate.Type, StringComparer.Ordinal)).ToList();
        if (removed.Count == 0)
        {
            return [];
        }

        ReplaceWithholdings([.. current.Except(removed)]);
        Touch(updatedBy, occurredAt);
        return [.. removed.Where(rate => rate.Overridden)];
    }

    public void AddItem(
        OrderItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        OrderLineCosts costs,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        // Los del producto en el catálogo al agregar la línea (spec 2026-11-18 §3.4); default = el valor de hoy.
        ProductKind kind = ProductKind.Physical,
        SalesUnit salesUnit = SalesUnit.Unit)
    {
        EnsureLinesEditable();
        ArgumentNullException.ThrowIfNull(costs);
        EnsureEditable("Products can only be added while the order is editable.");
        // Un pedido pagado sigue editable: la línea nueva ya cuenta como vendida y congela al entrar (§4.3). El
        // costo se pide antes de agregarla para que un costo sin buscar no deje la línea a medias.
        var sold = PaymentStatus == OrderPaymentStatus.FullPaymentReceived;
        var unitCost = sold ? costs.For(variantId) : null;
        var item = AddItemCore(
            itemId, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            QuotationDiscountOrigin.Own, null, kind, salesUnit, occurredAt);
        if (sold)
        {
            item.FreezeCost(unitCost, occurredAt);
        }

        Touch(updatedBy, occurredAt);
    }

    /// <summary>Agrega una línea de regalo (spec 2026-11-21): precio de lista real con descuento fijo en 100 %. Misma regla
    /// de edición, costo congelado y duplicado que <see cref="AddItem"/>, pero la clave es (variante, regalo): puede ser
    /// de la misma variante que una línea pagada.</summary>
    public void AddGift(
        OrderItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal listUnitPrice,
        int taxPercentage,
        OrderLineCosts costs,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        ProductKind kind = ProductKind.Physical,
        SalesUnit salesUnit = SalesUnit.Unit)
    {
        EnsureLinesEditable();
        // Un pedido mayorista no admite regalos: su precio sale de la orden de compra del distribuidor y la factura no los lleva.
        if (Source == OrderSource.Wholesale)
        {
            throw new QuotationsDomainException(
                "order.gift.wholesale_not_supported",
                "Un pedido mayorista no admite regalos.");
        }

        ArgumentNullException.ThrowIfNull(costs);
        EnsureEditable("Gifts can only be added while the order is editable.");
        var sold = PaymentStatus == OrderPaymentStatus.FullPaymentReceived;
        var unitCost = sold ? costs.For(variantId) : null;
        var item = AddItemCore(
            itemId, variantId, productId, quantity, listUnitPrice, OrderItem.GiftDiscountPercentage, taxPercentage,
            QuotationDiscountOrigin.Own, null, kind, salesUnit, occurredAt, isGift: true);
        if (sold)
        {
            item.FreezeCost(unitCost, occurredAt);
        }

        Touch(updatedBy, occurredAt);
    }

    // Congela las líneas que todavía no lo hicieron; las ya congeladas conservan su costo (§4.3). Resuelve todos
    // los costos antes de tocar una línea, para que un costo sin buscar no deje el pedido congelado a medias.
    private void FreezeLineCosts(OrderLineCosts costs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(costs);
        var pending = _items
            .Where(item => item.CostCapturedAt is null)
            .Select(item => (Item: item, Cost: costs.For(item.VariantId)))
            .ToArray();
        foreach (var (item, cost) in pending)
        {
            item.FreezeCost(cost, at);
        }
    }

    private OrderItem AddItemCore(
        OrderItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        QuotationDiscountOrigin origin,
        QuotationProductSnapshot? product,
        ProductKind kind,
        SalesUnit salesUnit,
        DateTimeOffset occurredAt,
        bool isGift = false)
    {
        // Una línea por variante (spec 2026-10-20 §4): dos tallas del mismo producto son dos líneas. El
        // código de error se conserva: es contrato con el frontend.
        if (_items.Any(existing => existing.VariantId == variantId && existing.IsGift == isGift))
        {
            throw new QuotationsDomainException(
                "order.item.duplicate_product",
                "La variante ya está en el pedido.");
        }

        // taxPercentage es la tasa del producto; la línea aplica el IVA de la empresa (spec 2026-11-17, R1).
        var item = OrderItem.Create(
            itemId, Id, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage, ChargesVat,
            _items.Count + 1, Currency, PriceRounding, kind, salesUnit, occurredAt, isGift);
        if (origin != QuotationDiscountOrigin.Own)
        {
            item.ApplyDiscount(discountPercentage, origin, Currency, PriceRounding, occurredAt);
        }

        if (product is not null)
        {
            item.CaptureProduct(product);
        }

        _items.Add(item);
        return item;
    }

    public void UpdateItemQuantity(
        OrderItemId itemId,
        decimal quantity,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureLinesEditable();
        EnsureEditable("Products can only be edited while the order is editable.");
        FindItem(itemId).UpdateQuantity(
            quantity, discountPercentage, taxPercentage, ChargesVat, Currency, PriceRounding, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    /// <summary>D-4: el precio por línea sólo se edita en un pedido mayorista, mientras sea editable. Cantidad, descuento y
    /// tasa no cambian; el precio pasa por la regla de redondeo del pedido.</summary>
    public void ChangeItemUnitPrice(OrderItemId itemId, decimal unitPrice, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureLinesEditable();
        if (Source != OrderSource.Wholesale)
        {
            throw new QuotationsDomainException(
                WholesaleOrderErrors.UnitPriceNotEditable, "Only a wholesale order lets you change a line price.");
        }

        EnsureEditable("Prices can only be changed while the order is editable.");
        FindItem(itemId).ChangeUnitPrice(unitPrice, ChargesVat, Currency, PriceRounding, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(OrderItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureLinesEditable();
        EnsureEditable("Products can only be removed while the order is editable.");
        // Un regalo no sostiene el pedido: sin una línea pagada no hay qué facturar. Quitar un regalo siempre se puede.
        var target = FindItem(itemId);
        if (!target.IsGift && _items.Count(item => !item.IsGift) == 1)
        {
            throw new QuotationsDomainException(
                "order.item.last_item_required",
                "The last remaining item cannot be removed from the order.");
        }

        _items.Remove(target);
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
    /// Cambia el envío de un pedido pendiente (spec 2026-10-17 flete §5.4). No cambia recoger o enviar: eso lo hace
    /// <see cref="SetDelivery"/>, que va antes en la misma edición. Prender la contraentrega exige un pago que todavía no
    /// empezó —sin comprobantes y <see cref="OrderPaymentStatus.PaymentPending"/>— (D-F14); ya prendida, se conserva
    /// aunque llegue el comprobante del giro.
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
        // Una empresa sin IVA no cobra IVA en el envío (spec 2026-11-17 §5.2): defensa para los pedidos externos y de la
        // tienda, que traen su envío armado.
        FreightTaxPercentage = ChargesVat ? shipping.FreightTaxPercentage : 0;
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
        foreach (var item in _items.Where(item => !item.IsGift))
        {
            if (discounts.TryGetValue(item.Id, out var discount))
            {
                item.ApplyDiscount(discount.Percentage, discount.Origin, Currency, PriceRounding, occurredAt);
            }
        }

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
    /// <see cref="OrderPaymentStatus.FullPaymentReceived"/>. Congela el costo de las líneas que todavía no lo tienen
    /// con <paramref name="costs"/>, que debe traer <see cref="UncostedVariantIds"/> (spec costo de producto §4.3).</summary>
    public void MarkPaid(MemberId markedBy, bool requirePaymentProof, OrderLineCosts costs, DateTimeOffset occurredAt)
    {
        EnsureCanMarkPaid(requirePaymentProof);
        FreezeLineCosts(costs, occurredAt);
        PaymentStatus = OrderPaymentStatus.FullPaymentReceived;
        PaidMarkedBy = markedBy;
        PaidMarkedAt = occurredAt;
        PaidAt = occurredAt;
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
        // Sale del reporte, pero las líneas conservan su costo: si se vuelve a marcar, reaparece con esos (§4.3).
        PaidAt = null;
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

        if (IsAwaitingWholesaleApproval)
        {
            throw new QuotationsDomainException(
                WholesaleOrderErrors.AwaitingApproval, "A wholesale order awaiting approval cannot be archived.");
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
        if (Status != OrderStatus.Open || PaymentStatus != OrderPaymentStatus.FullPaymentReceived || !IsPrepared
            || IsAwaitingWholesaleApproval)
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
    /// pedido. Tampoco enlaza si un asesor ya decidió la cuenta (<see cref="BuyerAccountSetByMember"/>).</summary>
    public bool LinkBuyerAccount(Guid buyerAccountId)
    {
        if (BuyerAccountId is not null || BuyerAccountSetByMember)
        {
            return false;
        }

        BuyerAccountId = buyerAccountId;
        return true;
    }

    /// <summary>Spec 2026-11-15 §4: el asesor cambia o quita (<c>null</c>) la cuenta de comprador de un pedido de la
    /// tienda, y su decisión queda marcada para que ningún enlace automático la deshaga. Es una edición del pedido: sube
    /// la versión. Devuelve <c>false</c> sin cambiar nada sólo si ya tenía esa misma cuenta por decisión de un asesor;
    /// quitar sobre un pedido sin cuenta sí marca, porque significa «no lo enlaces solo».</summary>
    public bool SetBuyerAccountByMember(Guid? buyerAccountId, DateTimeOffset occurredAt)
    {
        if (Source != OrderSource.Store)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only a store order has a buyer account.");
        }

        if (BuyerAccountSetByMember && BuyerAccountId == buyerAccountId)
        {
            return false;
        }

        BuyerAccountId = buyerAccountId;
        BuyerAccountSetByMember = true;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    public void CancelByBuyer(Guid buyerAccountId, DateTimeOffset occurredAt)
    {
        EnsureCancellableByBuyer(BuyerAccountId == buyerAccountId && CanBeCancelledByBuyer);
        ApplyBuyerCancellation(BuyerOrderErrors.CancellationReason, occurredAt);
    }

    /// <summary>
    /// D-12: el distribuidor anula su orden vinculada y, con ella, el pedido, mientras el mayorista no lo apruebe. Misma
    /// convención que <see cref="CancelByBuyer"/> (pre-flight X4): sin miembro detrás (<see cref="CancelledBy"/> null, que
    /// es lo que lee <see cref="CancelledByBuyer"/>), la misma guarda <see cref="CanBeCancelledByBuyer"/> y el mismo
    /// código. Devuelve <c>false</c> sin cambiar nada si ya estaba anulado (un reintento); los ids se comprueban antes,
    /// para que un comprador ajeno no aprenda nada del pedido.
    /// </summary>
    public bool CancelByWholesaleBuyer(Guid buyerTenantId, Guid purchaseOrderId, string reason, DateTimeOffset occurredAt)
    {
        EnsureCancellableByBuyer(
            Source == OrderSource.Wholesale && BuyerTenantId == buyerTenantId && PurchaseOrderId == purchaseOrderId);
        if (Status == OrderStatus.Cancelled)
        {
            return false;
        }

        EnsureCancellableByBuyer(WholesaleApprovedAt is null && CanBeCancelledByBuyer);
        ApplyBuyerCancellation(NormalizeCancellationReason(reason), occurredAt);
        return true;
    }

    /// <summary>D-1: el mayorista aprueba el pedido tal como quedó; desde ahí no se edita (D-3).</summary>
    public void ApproveWholesale(MemberId approvedBy, DateTimeOffset occurredAt)
    {
        if (Source != OrderSource.Wholesale)
        {
            throw new QuotationsDomainException(
                WholesaleOrderErrors.SourceInvalid, "Only a wholesale order is approved.");
        }

        EnsureOpen("Only an open order can be approved.");
        if (WholesaleApprovedAt is not null)
        {
            throw new QuotationsDomainException(
                WholesaleOrderErrors.AlreadyApproved, "The wholesale order is already approved.");
        }

        WholesaleApprovedAt = occurredAt;
        WholesaleApprovedBy = approvedBy;
        UpdatedAt = occurredAt;
        Version++;
    }

    private static void EnsureCancellableByBuyer(bool cancellable)
    {
        if (!cancellable)
        {
            throw new QuotationsDomainException(
                BuyerOrderErrors.NotCancellableByBuyer,
                "The order cannot be cancelled by this buyer.");
        }
    }

    private void ApplyBuyerCancellation(string reason, DateTimeOffset occurredAt)
    {
        Status = OrderStatus.Cancelled;
        CancelledBy = null;
        CancelledAt = occurredAt;
        CancellationReason = reason;
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

    /// <summary>
    /// Marca el pedido como facturado por un proveedor externo (spec 2026-11-18 §5.5). No exige pagado: el pago se validó una vez, al
    /// armar la factura, y ésta ya existe en el proveedor; marcar tiene que salir aunque un comprobante haya cambiado entre medio. Es
    /// <b>idempotente</b>: con la misma marca (mismo proveedor y número) no hace nada y no sube la versión. Con otra marca, manual o de
    /// otro número, es <c>order.order.already_invoiced</c>.
    /// </summary>
    public void InvoiceExternally(
        string provider, string number, string externalId, MemberId invoicedBy, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(provider.Length, InvoiceProviderMaxLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number.Length, InvoiceNumberMaxLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(externalId.Length, InvoiceExternalIdMaxLength);

        if (IsInvoiced
            && string.Equals(InvoiceProvider, provider, StringComparison.Ordinal)
            && string.Equals(InvoiceNumber, number, StringComparison.Ordinal))
        {
            return;
        }

        EnsureNotCancelled("A cancelled order cannot be invoiced.");
        EnsureNotInvoiced("The order is already invoiced.");
        InvoicedBy = invoicedBy;
        InvoicedAt = occurredAt;
        InvoiceProvider = provider;
        InvoiceNumber = number;
        InvoiceExternalId = externalId;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Quita la marca de un proveedor externo cuya factura se anuló allá (spec 2026-11-25 §4). Sólo suelta la marca de <b>ese</b>
    /// proveedor y <b>ese</b> número. Sin marca, o con otra (manual, otro número u otro proveedor), no hace nada: la anulación ya está
    /// guardada en el proveedor y una marca puesta en medio es una declaración de la persona que no se borra desde aquí.
    /// </summary>
    /// <returns>Si el pedido perdió la marca.</returns>
    public bool RevertExternalInvoicing(string provider, string number, MemberId revertedBy, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        if (!IsInvoiced
            || !string.Equals(InvoiceProvider, provider, StringComparison.Ordinal)
            || !string.Equals(InvoiceNumber, number, StringComparison.Ordinal))
        {
            return false;
        }

        InvoicedBy = null;
        InvoicedAt = null;
        InvoiceProvider = null;
        InvoiceNumber = null;
        InvoiceExternalId = null;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    public void RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)
    {
        if (!IsInvoiced)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.NotInvoiced,
                "Only an invoiced order can have its invoicing reverted.");
        }

        // Esta acción sólo revierte la marca manual. La de un proveedor externo se quita con RevertExternalInvoicing, cuando QEP verifica
        // que la factura se anuló allá (spec 2026-11-25 §4); las notas crédito siguen fuera de alcance.
        if (InvoiceProvider is not null)
        {
            throw new QuotationsDomainException(
                OrderStateErrors.InvoicedExternally,
                "An order invoiced by an external provider cannot have its invoicing reverted from QEP.");
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
            PaidAt = null;
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
    /// uno anulado o facturado. Al llevarlo a pagado no pone la marca de pagado (no lo marcó una persona); al sacarlo de
    /// pagado limpia la que hubiera.
    ///
    /// Con <paramref name="autoArchive"/> (el ajuste del tenant), un pedido que queda pagado y ya estaba preparado se
    /// archiva sin persona detrás. Devuelve <c>true</c> si lo archivó.
    ///
    /// Hacia pagado congela el costo de las líneas que todavía no lo tienen con <paramref name="costs"/> (debe traer
    /// <see cref="UncostedVariantIds"/>) y fija <see cref="PaidAt"/>; hacia otro estado lo limpia y los costos se
    /// quedan (spec costo de producto §4.3–§4.4).
    /// </summary>
    public bool SyncExternalPaymentStatus(
        OrderPaymentStatus status, bool autoArchive, OrderLineCosts costs, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(costs);
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

        if (status == OrderPaymentStatus.FullPaymentReceived)
        {
            FreezeLineCosts(costs, occurredAt);
            PaidAt = occurredAt;
        }
        else
        {
            // Una marca de pagado describe un pagado: si el pago sale de ahí, la marca se va con él.
            PaidMarkedBy = null;
            PaidMarkedAt = null;
            PaidAt = null;
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
    /// Spec 2026-11-16 §3 (F-4, F-5): corrige la «Fecha del pedido» con el patrón de <see cref="UpdateNotes"/>. Sólo en un
    /// pedido <see cref="IsEditable"/>, y nunca después de <paramref name="today"/>, que es hoy en el calendario del tenant
    /// (lo resuelve Application); hacia atrás no hay tope. <c>false</c> si es la misma fecha: el guardado responde «sin
    /// cambios» sin subir la versión ni escribir historial. No toca <see cref="ConvertedAt"/> ni recalcula impuestos (§8, P-1).
    /// </summary>
    public bool ChangeOrderDate(DateOnly date, DateOnly today, DateTimeOffset occurredAt)
    {
        EnsureEditable("The order date can only be edited on an editable order.");
        // Antes que el tope: reenviar la fecha ya guardada nunca es un cambio, aunque haya quedado después de «hoy».
        if (date == OrderDate)
        {
            return false;
        }

        if (date > today)
        {
            throw new QuotationsDomainException(
                "order.order_date.in_future",
                "The order date cannot be later than today in the tenant calendar.");
        }

        OrderDate = date;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    /// <summary>
    /// Spec 2026-11-14: cambia la nota del comprador de un pedido de Shopify (la sincronización desde Shopify y la
    /// corrección del asesor). Sólo pedidos externos (<see cref="OrderSource.Shopify"/>): la nota de un pedido de la tienda
    /// pública es lo que el comprador escribió y no se toca. <c>true</c> si cambió; vacío o sólo espacios es <c>null</c>
    /// (D22), así que mandar <c>""</c> sobre una nota nula no cambia nada. No toca <see cref="Notes"/>.
    /// </summary>
    public bool UpdateExternalBuyerNotes(string? buyerNotes, DateTimeOffset occurredAt)
    {
        if (!IsExternal)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only an external order takes its buyer notes from the platform.");
        }

        EnsureEditable("The buyer notes can only change while the order is editable.");
        var normalized = NormalizeNotes(buyerNotes);
        if (string.Equals(normalized, BuyerNotes, StringComparison.Ordinal))
        {
            return false;
        }

        BuyerNotes = normalized;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    /// <summary>
    /// Spec 2026-10-26 §4.4: agrega la parte de envío que Shopify no pudo completar al crear el pedido (la ciudad
    /// no se reconocía). <c>true</c> si la agregó; <c>false</c> si ya estaba esa misma parte en una de
    /// <paramref name="requestedCityIds"/>, las ciudades que el consumidor pidió hasta ahora (un reintento, desvío S1: la puso un
    /// intento anterior con la ciudad que el sinónimo tenía entonces; el consumidor lee la que quedó en <see cref="Shipping"/>).
    /// En una ciudad que nadie pidió la puso una persona por la entrega editable o la corrección del panel: es otra parte
    /// (<c>order.order.shipping_party_present</c>).
    /// Quita de las notas toda línea que empiece por <paramref name="notesLinePrefix"/> (ordinal): la dirección en
    /// crudo que se anotó mientras no había parte. Sólo pedidos externos pendientes, sin recogida en tienda y sin
    /// otra parte de envío; la parte debe quedar completa. No recalcula totales.
    /// </summary>
    public bool AttachExternalShipping(
        QuotationPartyDetails shipping, string notesLinePrefix, IReadOnlyCollection<Guid> requestedCityIds, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        ArgumentException.ThrowIfNullOrEmpty(notesLinePrefix);
        ArgumentNullException.ThrowIfNull(requestedCityIds);
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
            return SameExceptCity(current.ToDetails(), candidate.ToDetails()) && IsRequestedCity(current, requestedCityIds)
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
    /// Spec 2026-11-14 §5: el asesor corrige la parte de envío de un pedido de Shopify (nombre, teléfono, correo, dirección,
    /// departamento y ciudad). Si el pedido no la tenía, la crea y quita de las notas toda línea que empiece por
    /// <paramref name="notesLinePrefix"/> (la dirección cruda de la importación); si la tenía, la reemplaza en su fila.
    /// <c>true</c> si cambió; la misma parte no toca nada. Sólo pedidos externos editables, sin recogida en tienda, y la parte
    /// debe quedar completa. Fuera de este camino, la parte de envío sólo cambia por la entrega editable
    /// (<see cref="SetDelivery"/>).
    /// </summary>
    public bool EditExternalShippingParty(QuotationPartyDetails party, string notesLinePrefix, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentException.ThrowIfNullOrEmpty(notesLinePrefix);
        if (!IsExternal)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only an external order has its shipping party corrected for the platform.");
        }

        EnsureEditable("The shipping party can only be corrected while the order is editable.");
        if (IsStorePickup)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.StorePickup, "An order picked up at the store has no shipping party.");
        }

        var candidate = OrderParty.Create(Id, QuotationPartyRole.Shipping, party);
        if (!candidate.IsComplete)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.ShippingPartyIncomplete, "The shipping party must have all its fields filled.");
        }

        if (Shipping is { } current)
        {
            if (current.ToDetails() == candidate.ToDetails())
            {
                return false;
            }

            current.Apply(candidate.ToDetails());
        }
        else
        {
            _parties.Add(candidate);
            Notes = NormalizeNotes(string.Join(
                '\n',
                (Notes ?? string.Empty).Split('\n').Where(line => !line.StartsWith(notesLinePrefix, StringComparison.Ordinal))));
        }

        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    /// <summary>
    /// Spec 2026-10-26 §4.6: cambia sólo el departamento y la ciudad de la parte de envío, y sólo si sigue siendo
    /// la que se guardó (<paramref name="expected"/>) salvo la ciudad: si alguien tocó otro campo, o la llevó a una ciudad que no
    /// está en <paramref name="requestedCityIds"/> (una persona), lanza <c>order.order.shipping_party_changed</c>. <c>true</c> si
    /// cambió; <c>false</c> si la parte ya está en el destino o en otra de las ciudades que el consumidor pidió (un reintento: la
    /// llevó un intento anterior a la que el sinónimo tenía entonces y el consumidor cayó antes de actualizar su huella; lee la
    /// que quedó en <see cref="Shipping"/>).
    /// </summary>
    public bool CorrectExternalShippingCity(
        QuotationPartyDetails expected, Guid departmentId, Guid cityId, IReadOnlyCollection<Guid> requestedCityIds,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(requestedCityIds);
        EnsureEditable("The shipping city can only be corrected while the order is editable.");
        if (Shipping is not { } party
            || !SameExceptCity(party.ToDetails(), expected)
            || (party.ToDetails() != expected && !IsRequestedCity(party, requestedCityIds)))
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

    /// <summary>
    /// Spec 2026-11-14 §4: aplica una edición que hizo la plataforma (Shopify) sobre un pedido externo editable. Sólo cambia los
    /// grupos que llegan y que de verdad son distintos de lo que el pedido tiene; sin diferencias no toca nada (ni la versión).
    /// <list type="bullet">
    /// <item>Las líneas llegan preciadas: se reemplazan todas, sin repreciar ni aplicar descuentos de catálogo. La que sigue
    /// (misma variante) conserva su id y su costo congelado; con el pedido pagado, la nueva congela su costo con
    /// <paramref name="costs"/> (§4.3).</item>
    /// <item>El envío pasa por las reglas de coherencia del pedido (<see cref="ShippingDetails"/>).</item>
    /// <item>La parte de envío se modifica en su fila; si el pedido no la tenía, se crea y se quitan de las notas las líneas que
    /// empiezan por <see cref="ExternalOrderEdit.NotesLinePrefix"/>. Un pedido de recogida no la admite.</item>
    /// <item>La nota del comprador va a <see cref="BuyerNotes"/>; <see cref="Notes"/> no se toca (salvo la línea cruda de la
    /// dirección).</item>
    /// </list>
    /// Todo se valida antes de cambiar nada, y la versión sube una sola vez. No recalcula el estado de pago: el pago de un pedido
    /// externo lo lleva la plataforma (plan 2026-11-14, D18).
    /// </summary>
    public ExternalOrderEditResult ApplyExternalEdit(ExternalOrderEdit edit, OrderLineCosts costs, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(costs);
        EnsureEditable("An external edit can only be applied while the order is editable.");
        if (!IsExternal)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only an external order takes its edits from the platform.");
        }

        OrderParty? party = null;
        if (edit.ShippingParty is { } details)
        {
            if (IsStorePickup)
            {
                throw new QuotationsDomainException(
                    ExternalOrderErrors.StorePickup, "An order picked up at the store has no shipping party.");
            }

            party = OrderParty.Create(Id, QuotationPartyRole.Shipping, details);
            if (!party.IsComplete)
            {
                throw new QuotationsDomainException(
                    ExternalOrderErrors.ShippingPartyIncomplete, "The shipping party must have all its fields filled.");
            }
        }

        var shipping = edit.Shipping?.Validated(IsStorePickup, Currency, "order.order");
        if (shipping is { CashOnDelivery: true } && !CashOnDelivery
            && (_paymentProofs.Count > 0 || PaymentStatus != OrderPaymentStatus.PaymentPending))
        {
            throw CashOnDeliveryPendingOnly();
        }

        var lines = edit.Lines is null ? null : CandidateLines(edit.Lines, occurredAt);
        var buyerNotes = NormalizeNotes(edit.BuyerNotes);
        var result = new ExternalOrderEditResult(
            lines is not null && !SameItems(lines),
            shipping is not null && shipping != ShippingDetails,
            party is not null && Shipping?.ToDetails() != party.ToDetails(),
            edit.ReplaceBuyerNotes && !string.Equals(buyerNotes, BuyerNotes, StringComparison.Ordinal));
        if (!result.Any)
        {
            return result;
        }

        if (result.LinesChanged)
        {
            ReplaceExternalLines(lines!, edit.Lines!, costs, occurredAt);
        }

        if (result.ShippingChanged)
        {
            AssignShipping(shipping!);
        }

        if (result.PartyChanged)
        {
            if (Shipping is { } current)
            {
                current.Apply(party!.ToDetails());
            }
            else
            {
                _parties.Add(party!);
                Notes = NormalizeNotes(string.Join(
                    '\n',
                    (Notes ?? string.Empty).Split('\n')
                        .Where(line => !line.StartsWith(edit.NotesLinePrefix, StringComparison.Ordinal))));
            }
        }

        if (result.BuyerNotesChanged)
        {
            BuyerNotes = buyerNotes;
        }

        RecalculateTotals();
        UpdatedAt = occurredAt;
        Version++;
        return result;
    }

    // Las líneas como se guardarían (precio redondeado, IVA según la moneda), sin agregarlas: valida cantidad, precio, descuento
    // y tasa antes de tocar el pedido, y deja comparar contra lo guardado.
    private OrderItem[] CandidateLines(IReadOnlyCollection<OrderLineInput> lines, DateTimeOffset occurredAt)
    {
        if (lines.Count == 0)
        {
            throw new QuotationsDomainException("order.order.items_required", "An order needs at least one product.");
        }

        if (lines.Select(line => line.VariantId).Distinct().Count() != lines.Count)
        {
            throw new QuotationsDomainException("order.item.duplicate_product", "La variante ya está en el pedido.");
        }

        return lines
            // Las líneas de una plataforma externa nacen físicas y por unidad (spec 2026-11-18 §3.4).
            .Select((line, index) => OrderItem.Create(
                OrderItemId.New(), Id, line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage,
                line.TaxPercentage, ChargesVat, index + 1, Currency, PriceRounding, ProductKind.Physical, SalesUnit.Unit,
                occurredAt))
            .ToArray();
    }

    private bool SameItems(OrderItem[] candidates) =>
        // Los regalos tienen su propio alta y baja: la edición externa sólo compara las líneas pagadas.
        candidates.Length == _items.Count(item => !item.IsGift)
        && candidates.All(candidate => _items.Any(item => item.SameAs(candidate)));

    private void ReplaceExternalLines(
        OrderItem[] candidates, IReadOnlyCollection<OrderLineInput> lines, OrderLineCosts costs, DateTimeOffset occurredAt)
    {
        // El costo se busca antes de tocar una línea, para que un costo sin buscar no deje el pedido a medias (§4.3).
        var sold = PaymentStatus == OrderPaymentStatus.FullPaymentReceived;
        var newCosts = sold
            ? candidates
                .Where(candidate => _items.All(item => item.IsGift || item.VariantId != candidate.VariantId))
                .ToDictionary(candidate => candidate.VariantId, candidate => costs.For(candidate.VariantId))
            : [];
        var inputs = lines.ToArray();
        _items.RemoveAll(item => !item.IsGift && candidates.All(candidate => candidate.VariantId != item.VariantId));
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var input = inputs[index];
            if (_items.FirstOrDefault(item => !item.IsGift && item.VariantId == candidate.VariantId) is { } kept)
            {
                kept.ApplyExternal(
                    input.Quantity, input.UnitPrice, input.DiscountPercentage, input.TaxPercentage, ChargesVat, index + 1,
                    Currency, PriceRounding, occurredAt);
                if (input.Product is { } snapshot)
                {
                    kept.CaptureProduct(snapshot);
                }

                continue;
            }

            if (input.Product is { } product)
            {
                candidate.CaptureProduct(product);
            }

            if (sold)
            {
                candidate.FreezeCost(newCosts[candidate.VariantId], occurredAt);
            }

            _items.Add(candidate);
        }

        // Las pagadas conservan su orden y los regalos van después, también en su orden: nunca dos líneas con la misma posición.
        var position = 1;
        foreach (var line in _items.Where(item => !item.IsGift).OrderBy(item => item.Position)
                     .Concat(_items.Where(item => item.IsGift).OrderBy(item => item.Position)).ToArray())
        {
            line.MoveTo(position++);
        }
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

    // Spec 2026-10-26 y desvío S1: una parte de envío igual salvo el departamento y la ciudad puede ser la que puso la
    // plataforma en otra ciudad del mismo sinónimo. Sólo lo es si además esa ciudad la pidió el consumidor (IsRequestedCity):
    // desde la entrega editable, una persona cambia sólo la ciudad.
    private static bool SameExceptCity(QuotationPartyDetails party, QuotationPartyDetails other) =>
        party with { DepartmentId = other.DepartmentId, CityId = other.CityId } == other;

    // La misma señal con que Customers distingue el reintento de un trabajo de lo que hizo una persona.
    private static bool IsRequestedCity(OrderParty party, IReadOnlyCollection<Guid> requestedCityIds) =>
        party.CityId is { } cityId && requestedCityIds.Contains(cityId);

    // El alcance vendido y su línea no se cambian en el pedido (spec 2026-11-26, P4): se anula y se cotiza de nuevo.
    private void EnsureLinesEditable()
    {
        if (Kind == QuotationKind.Project)
        {
            throw new QuotationsDomainException(
                ProjectErrors.ItemsLocked,
                "En un pedido de proyecto no puedes cambiar productos ni regalos. Para cambiar lo vendido, anula y cotiza de nuevo.");
        }
    }

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

    // Misma aritmética y misma regla de candidatas que la cotización (DocumentTotals, WithholdingCandidates): cada
    // fila de la foto se liquida otra vez, también las que no aplican.
    private void RecalculateTotals()
    {
        var totals = DocumentTotals.Compute(
            _items.Select(item => item.Amounts),
            FreightAmounts.For(FreightChargeMode, FreightAmount, FreightTaxPercentage, Currency, PriceRounding),
            AppliesVatSurplus,
            WithholdingCandidates.For(
                _withholdings.OrderBy(withholding => withholding.Position).Select(withholding => withholding.Rate),
                BillsToFinalConsumer,
                BillingAccount,
                Currency,
                OperationCityId));
        DocumentWithholding.SettleAll(_withholdings, WithholdingSource.Document, totals.Withholdings);
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

    private void ReplaceWithholdings(IReadOnlyList<WithholdingRate> rates) =>
        DocumentWithholding.ReplaceSource(_withholdings, WithholdingSource.Document, rates);

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
