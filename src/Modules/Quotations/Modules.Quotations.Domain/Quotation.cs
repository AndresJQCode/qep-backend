using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Una cotización de un tenant (modelo-datos-cotizaciones.md §2.1). Agregado raíz que incluye sus
/// líneas de producto (<see cref="QuotationItem"/>) — toda mutación de líneas pasa por sus
/// métodos, que recalculan los totales del encabezado, mismo criterio que
/// <c>Product</c>/<c>PriceScale</c> en Catalog.
///
/// Cubre el borrador (crear, agregar/editar/quitar líneas, editar encabezado), el envío (US-12),
/// la anulación (US-11), la conversión a pedido (US-16, vía <see cref="ConvertToOrder"/>, que la
/// deja en <see cref="QuotationStatus.Converted"/>) y el vencimiento automático (US-19).
///
/// Editar (líneas o encabezado) exige <see cref="QuotationStatus.Draft"/> o
/// <see cref="QuotationStatus.Sent"/> — US-10: "se puede editar en Draft y Sent... se bloquea
/// una vez convertida a pedido, anulada o vencida". <see cref="Send"/> sale de Draft o de Sent
/// (reenvío); <see cref="Void"/> y <see cref="ConvertToOrder"/> también.
///
/// Convertir es copiar (spec 2026-10-15 §4.4): el pedido nace con sus propias líneas, partes y
/// totales, y la cotización <see cref="QuotationStatus.Converted"/> queda congelada como el
/// documento que el cliente vio. Editar el pedido pendiente edita el pedido, nunca esta
/// cotización.
/// </summary>
public sealed class Quotation
{
    public const int QuotationNumberMaxLength = 20;
    public const int PaymentMethodMaxLength = 50;

    private readonly List<QuotationItem> _items = [];

    private readonly List<QuotationParty> _parties = [];

    // La regla con que se preció el documento (spec 2026-10-27 §4). Nulas en un documento anterior a la
    // regla: se leen con la de siempre de su moneda (D4). EF las materializa por el campo.
    private int? _priceRoundingDecimals;
    private PriceRoundingMode? _priceRoundingMode;

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private Quotation()
    {
        QuotationNumber = string.Empty;
    }

    private Quotation(
        QuotationId id,
        Guid tenantId,
        string quotationNumber,
        Guid clientId,
        MemberId advisorId,
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        string defaultCurrency,
        bool customerWithRetention,
        bool customerVatSurplus,
        MemberId createdBy,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        QuotationNumber = NormalizeQuotationNumber(quotationNumber);
        ClientId = EnsureValidClientId(clientId);
        AdvisorId = advisorId;
        Status = QuotationStatus.Draft;
        CreatedAt = occurredAt;
        ValidUntil = validUntil;
        PaymentMethod = NormalizePaymentMethod(paymentMethod);
        Notes = NormalizeNotes(notes);
        EnsureBillingIsConsistent(parties);
        Assign(parties);
        // Crear es escribir: no hay cálculo previo de la creación que proteger.
        EnsureBillingIdentified();
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        IsStorePickup = parties.IsStorePickup;
        BillsToFinalConsumer = parties.BillsToFinalConsumer;
        PartyWithRetention = parties.Billing is null ? null : parties.BillingWithRetention;
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        BillingAccount = billingAccount?.Normalized();
        // The billing account rules when there is one; otherwise the tenant default, read and
        // normalised against the catalogue by CreateQuotationHandler (this assembly cannot see it).
        Currency = BillingAccount?.Currency ?? defaultCurrency;
        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        CreatedBy = createdBy;
        UpdatedAt = occurredAt;
        Version = 1;
        RecalculateTotals();
    }

    public QuotationId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant. Formato <c>QUO-2025-0423</c>, emitido por
    /// <c>IQuotationNumberGenerator</c> — el dominio sólo comprueba que llegue y quepa.</summary>
    public string QuotationNumber { get; private set; }

    /// <summary>Referencia blanda al módulo Customers. Sin FK a propósito: ningún módulo de
    /// negocio referencia las tablas de otro. Que el cliente tenga CUC y esté activo lo valida
    /// la aplicación con <c>IQuotationCustomerLookup</c> antes de construir el agregado.</summary>
    public Guid ClientId { get; private set; }

    /// <summary>Referencia blanda a un <c>Membership</c> del módulo Tenancy (§1.4 del modelo de
    /// datos).</summary>
    public MemberId AdvisorId { get; private set; }

    public QuotationStatus Status { get; private set; }

    /// <summary>
    /// El piso de escala que el asesor eligió para toda la cotización, o <c>null</c> si no
    /// eligió ninguno. No es un porcentaje: es el <c>FromUnit</c> de un tramo, y cada línea
    /// resuelve contra el tramo de **su** producto que arranca ahí.
    ///
    /// Vive en la cotización y no en el pedido porque el pedido no tiene líneas propias: las
    /// suyas son éstas.
    /// </summary>
    public int? GlobalScaleFloor { get; private set; }

    /// <summary>
    /// Cotización detal: ninguna línea recibe descuento, cualesquiera sean las escalas de su
    /// producto y la cantidad pedida. No es un cero por ciento elegido a mano línea por línea
    /// —eso lo pisaría el primer recálculo—; es el corte de arriba del recálculo, en
    /// <c>QuotationPricingRecalculation.ApplyAsync</c>.
    ///
    /// El pedido lleva su propio interruptor (<c>Order.IsRetail</c>), copiado al convertir.
    ///
    /// Es excluyente con <see cref="GlobalScaleFloor"/> — ver <see cref="SetIsRetail"/> y
    /// <see cref="SetGlobalScaleFloor"/>.
    /// </summary>
    public bool IsRetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateOnly? ValidUntil { get; private set; }

    public string? PaymentMethod { get; private set; }

    /// <summary>Suma de los subtotales de línea, ya netos del descuento de cada una.</summary>
    public decimal Subtotal { get; private set; }

    /// <summary>Tasa efectiva de la cotización: <c>TaxAmount / Subtotal × 100</c>, derivada de
    /// la suma del impuesto de cada línea (<see cref="RecalculateTotals"/>) — no es un valor que
    /// se pueda fijar desde fuera, cada producto trae su propia tasa
    /// (<c>Catalog.TaxRate.Percentage</c>). 0 sin líneas o sin subtotal, y 0 en USD: en dólares
    /// las líneas no discriminan el IVA (<see cref="QuotationItem.TaxAmount"/>), así que
    /// <see cref="Total"/> es igual a <see cref="Subtotal"/>.</summary>
    public decimal TaxPercentage { get; private set; }

    public decimal TaxAmount { get; private set; }

    /// <summary>Suma de los descuentos de línea. Informativo — "cuánto ahorró el cliente": ya
    /// está reflejado dentro de <see cref="Subtotal"/> y nunca se resta del <see cref="Total"/>
    /// (modelo-datos-cotizaciones.md §1.6).</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Subtotal + TaxAmount. El descuento no se resta de nuevo: contarlo dos veces
    /// sería el error que §1.6 previene explícitamente. No refleja la retención — sigue siendo
    /// lo facturado, no lo cobrado en efectivo (ver <see cref="NetTotal"/>).</summary>
    public decimal Total { get; private set; }

    /// <summary>Copia de <c>Customer.WithRetention</c>, resuelta al crear la cotización y
    /// vuelta a tomar del cliente maestro mientras sigue editable (<see cref="QuotationStatus.Draft"/>/
    /// <see cref="QuotationStatus.Sent"/>, ver <see cref="RefreshCustomerTaxProfile"/>) — a
    /// diferencia de los overrides de facturación/entrega de abajo (una decisión manual de
    /// quien cotiza), esto es un hecho del cliente que puede cambiar, y no un valor a congelar
    /// a propósito. Se fija tal cual quedó una vez Voided o Expired. La usa
    /// <see cref="RecalculateTotals"/> para <see cref="RetentionAmount"/>.</summary>
    public bool CustomerWithRetention { get; private set; }

    /// <summary>Igual criterio que <see cref="CustomerWithRetention"/> pero para
    /// <c>Customer.VatSurplus</c>. La usa <see cref="RecalculateTotals"/> para eximir de IVA a
    /// esta cotización entera.</summary>
    public bool CustomerVatSurplus { get; private set; }

    /// <summary>Retención en la fuente: <c>round(Subtotal * 0.025)</c> cuando
    /// <see cref="AppliesRetention"/>, si no 0 (también 0 en USD). Informativo hacia <see cref="Total"/> (que
    /// sigue siendo lo facturado) pero resta hacia <see cref="NetTotal"/> — es lo que el cliente
    /// le retiene al vendedor, no algo que la cotización deje de facturar.</summary>
    public decimal RetentionAmount { get; private set; }

    /// <summary>Total - RetentionAmount: lo que efectivamente se cobra en efectivo. Igual a
    /// <see cref="Total"/> cuando no hay retención.</summary>
    public decimal NetTotal { get; private set; }

    /// <summary>Subtotal del envío, sin IVA. Cero salvo que el envío se cobre en el documento.</summary>
    public decimal FreightSubtotal { get; private set; }

    /// <summary>IVA del envío. Cero con excedente de IVA o fuera de COP.</summary>
    public decimal FreightTaxAmount { get; private set; }

    /// <summary>Lo facturado por los productos, sin el envío.</summary>
    public decimal ProductsTotal => Subtotal + TaxAmount;

    /// <summary>La transportadora elegida. Sin FK a <c>carriers</c>: la cotización no depende de que la
    /// transportadora siga igual (spec 2026-10-17 flete §5.1).</summary>
    public Guid? CarrierId { get; private set; }

    /// <summary>Foto del nombre de la transportadora al elegirla: es lo que se muestra, se exporta y se imprime.</summary>
    public string? CarrierName { get; private set; }

    /// <summary>Cómo se cobra el envío. Null = sin envío registrado.</summary>
    public FreightChargeMode? FreightChargeMode { get; private set; }

    /// <summary>El valor del envío con IVA incluido, como lo escribió el asesor. Sólo suma al total con
    /// <see cref="Domain.FreightChargeMode.Charged"/>.</summary>
    public decimal FreightAmount { get; private set; }

    /// <summary>Foto de la tasa del envío al fijar el valor; 0 fuera de COP.</summary>
    public int FreightTaxPercentage { get; private set; }

    /// <summary>Si el pedido que salga de esta cotización se cobra contraentrega (spec §6).</summary>
    public bool CashOnDelivery { get; private set; }

    /// <summary>Los seis campos del envío juntos. Se llama así, y no <c>Shipping</c>, porque ese nombre ya
    /// es la parte de entrega (<see cref="Shipping"/>).</summary>
    public ShippingDetails ShippingDetails =>
        new(CarrierId, CarrierName, FreightChargeMode, FreightAmount, FreightTaxPercentage, CashOnDelivery);

    public string? Notes { get; private set; }

    public MemberId CreatedBy { get; private set; }

    public MemberId? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cuándo se marcó como enviada (US-12). Null hasta ese momento.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Referencia blanda al archivo PDF en el módulo Storage — el mismo mecanismo que
    /// <c>Product.ImageFileId</c>. Sin FK a propósito: ningún módulo de negocio referencia las
    /// tablas de otro. Que el archivo exista, sea del tenant y ya haya terminado de subir lo
    /// valida la aplicación con <c>IQuotationFileLookup</c> antes de llamar a <see cref="Send"/>.
    ///
    /// Se guarda el id y no una URL (a diferencia de <c>pdf_url</c> en el modelo de datos
    /// compartido): Storage no expone URLs permanentes salvo que el archivo esté publicado en el
    /// bucket público, y un documento de negocio como una cotización no es ese caso — la URL de
    /// descarga se resuelve bajo demanda contra el endpoint que Storage ya expone.
    /// </summary>
    /// <summary>Histórico: el `FileResource` que el navegador subía cuando el PDF lo generaba
    /// el frontend. Las cotizaciones enviadas desde entonces lo dejan en null -- el documento
    /// vive en `quotation_pdfs`. Se conserva para no reescribir lo que ya pasó.</summary>
    public Guid? PdfFileId { get; private set; }

    /// <summary>Token de concurrencia optimista, mismo criterio que Product/Customer.</summary>
    public long Version { get; private set; }

    public IReadOnlyCollection<QuotationItem> Items => _items;

    /// <summary>A quién se le factura y a quién se le entrega, **sólo cuando no son los datos del
    /// cliente** (US-6): una parte ausente es "usá los del cliente". Ver
    /// <see cref="QuotationParty"/>.</summary>
    public IReadOnlyCollection<QuotationParty> Parties => _parties;

    /// <summary>Con qué empresa y a qué cuenta se cobra esta cotización, congelado al guardarla
    /// (<see cref="QuotationBillingAccount"/>). Null mientras nadie la eligió.</summary>
    public QuotationBillingAccount? BillingAccount { get; private set; }

    /// <summary>
    /// ISO 4217 code from the catalogue (Modules.Tenancy.Application.Currencies) of the **whole**
    /// quotation: unit prices, discounts, taxes and totals.
    ///
    /// La fija la cuenta de cobro (<see cref="BillingAccount"/>): si se factura a una cuenta en
    /// dólares, la cotización entera se expresa en dólares, con el precio en dólares de cada
    /// producto — no con una conversión, que este módulo no sabe hacer. Sin cuenta elegida es la
    /// moneda por defecto del tenant, que la aplicación lee al crear.
    ///
    /// Quitar la cuenta **no** devuelve la cotización a pesos: los precios que ya se cotizaron
    /// quedan como están, y revalorizarlos por un borrado sería un cambio de totales que nadie
    /// pidió. Vuelve a cambiar recién cuando se elige otra cuenta.
    /// </summary>
    public string Currency { get; private set; } = string.Empty;

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

    /// <summary>
    /// Cuando la facturación sigue los datos del cliente, si el nombre que va en el documento es
    /// su razón social en vez del de contacto (CLI-RS-01).
    ///
    /// Se resuelve **al leer** y no se congela: el switch prendido significa "seguí al cliente",
    /// así que si su razón social cambia, la cotización editable la refleja — mismo criterio que
    /// el resto de los datos que la parte ausente toma del cliente. Irrelevante cuando
    /// <see cref="Billing"/> tiene fila propia: ahí el nombre lo escribió alguien.
    /// </summary>
    public bool BillingUsesBusinessName { get; private set; }

    /// <summary>
    /// Si el cliente recoge en la tienda en vez de recibir el pedido en una dirección.
    ///
    /// Invariante: con esto prendido <see cref="Shipping"/> es siempre null — no hay a dónde
    /// entregar, y una fila de envío guardada al lado diría dos cosas distintas de la misma
    /// entrega. Por eso no es "una parte más": <see cref="Assign(QuotationParties)"/> descarta
    /// la de entrega cuando llega junto con esto, y prenderlo borra la que hubiera.
    ///
    /// A diferencia de las partes, <see cref="ChangeClient"/> no lo toca: las partes se borran
    /// porque copiaron al cliente viejo, y recoger en tienda no copia nada de ningún cliente.
    /// </summary>
    public bool IsStorePickup { get; private set; }

    /// <summary>
    /// Si la factura sale a nombre de <see cref="FinalConsumer"/> en vez de a los datos del
    /// cliente o a una parte propia. La cotización sigue teniendo su cliente real —a quien se le
    /// cotiza, se le envía y se le entrega—: esto sólo cambia a nombre de quién se factura.
    ///
    /// Invariante: con esto prendido <see cref="Billing"/> es null y
    /// <see cref="BillingUsesBusinessName"/> es false. Se rechaza, no se normaliza: un request que
    /// manda las dos cosas dice dos nombres distintos para la misma factura, y elegir uno en
    /// silencio escondería el error de quien lo mandó.
    ///
    /// Apaga la retención y el excedente de IVA (<see cref="AppliesRetention"/>,
    /// <see cref="AppliesVatSurplus"/>) sin tocar <see cref="CustomerWithRetention"/> ni
    /// <see cref="CustomerVatSurplus"/>: desmarcarlo los restituye sin volver a consultar al
    /// cliente.
    /// </summary>
    public bool BillsToFinalConsumer { get; private set; }

    /// <summary>Si quien recibe la factura practica retención en la fuente, cuando
    /// <see cref="Billing"/> trae datos propios — nadie más lo sabe con datos propios, así que se
    /// pregunta al armar la cotización (<see cref="QuotationParties.BillingWithRetention"/>). Null
    /// mientras <see cref="Billing"/> sigue los del cliente (ahí manda
    /// <see cref="CustomerWithRetention"/>) o mientras no se contestó. Es lo que usa
    /// <see cref="AppliesRetention"/> cuando hay parte propia.</summary>
    public bool? PartyWithRetention { get; private set; }

    /// <summary>Mismo criterio que <see cref="PartyWithRetention"/> pero para el excedente de IVA
    /// (<see cref="QuotationParties.BillingVatSurplus"/> / <see cref="CustomerVatSurplus"/>).</summary>
    public bool? PartyVatSurplus { get; private set; }

    /// <summary>Si la retención en la fuente se aplica a esta cotización. Con datos propios de
    /// facturación manda lo que se contestó ahí (<see cref="PartyWithRetention"/>, false mientras
    /// no se conteste); con los datos del cliente, manda <see cref="CustomerWithRetention"/>. En
    /// ambos casos, no a consumidor final, y nunca fuera de COP: ahí el IVA no se discrimina
    /// (<see cref="QuotationItem.TaxAmount"/>), así que no hay base sin IVA sobre la cual retener
    /// (owner, 2026-10-14). Es lo que usa <see cref="RecalculateTotals"/>.</summary>
    public bool AppliesRetention =>
        (Billing is not null ? PartyWithRetention ?? false : CustomerWithRetention)
        && !BillsToFinalConsumer
        && QuotationItem.ItemizesTaxIn(Currency);

    /// <summary>Si el excedente de IVA exime a esta cotización, con el mismo criterio de fuente
    /// que <see cref="AppliesRetention"/> pero para <see cref="PartyVatSurplus"/> /
    /// <see cref="CustomerVatSurplus"/>. Es lo que usa <see cref="RecalculateTotals"/> y lo que
    /// tiene que leer quien imprima "exento".</summary>
    public bool AppliesVatSurplus =>
        (Billing is not null ? PartyVatSurplus ?? false : CustomerVatSurplus) && !BillsToFinalConsumer;

    /// <summary>
    /// Si la cotización se editó desde la última vez que se envió. <see cref="Send"/> deja
    /// <see cref="SentAt"/> y <see cref="UpdatedAt"/> en el mismo instante, y toda edición mueve
    /// el segundo — así que uno mayor que el otro es exactamente "cambió después de enviarse".
    /// </summary>
    public bool HasChangesSinceSent => SentAt is { } sentAt && UpdatedAt > sentAt;

    /// <summary>
    /// Si el botón de enviar tiene sentido ahora. La regla vive acá y no en la pantalla para que
    /// las dos no puedan discrepar: un borrador y una ya enviada, haya cambiado o no.
    ///
    /// Antes una enviada sin cambios quedaba afuera —"no hay nada nuevo que mandar"—, pero eso
    /// confundía el contenido del mensaje con el hecho de entregarlo: al cliente se le pierde un
    /// WhatsApp, lo borra o cambia de teléfono, y volver a mandárselo es exactamente lo que la
    /// persona necesita hacer. <see cref="HasChangesSinceSent"/> sigue existiendo para
    /// distinguir un reenvío de un envío con cambios en el historial.
    /// </summary>
    public bool CanBeSent => Status is QuotationStatus.Draft or QuotationStatus.Sent;

    public QuotationParty? Billing => FindParty(QuotationPartyRole.Billing);

    public QuotationParty? Shipping => FindParty(QuotationPartyRole.Shipping);

    public static Quotation Create(
        QuotationId id,
        Guid tenantId,
        string quotationNumber,
        Guid clientId,
        MemberId advisorId,
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        string defaultCurrency,
        bool customerWithRetention,
        bool customerVatSurplus,
        MemberId createdBy,
        DateTimeOffset occurredAt) =>
        new(
            id,
            tenantId,
            quotationNumber,
            clientId,
            advisorId,
            validUntil,
            paymentMethod,
            notes,
            parties,
            billingAccount,
            defaultCurrency,
            customerWithRetention,
            customerVatSurplus,
            createdBy,
            occurredAt);

    /// <summary>
    /// Agrega una línea (US-3). <paramref name="discountPercentage"/> ya viene resuelto por la
    /// aplicación contra la escala de precios del producto para <paramref name="quantity"/>
    /// (<c>IQuotationProductPricingLookup</c> + el resolver de escala) — el dominio no sabe nada
    /// de escalas de otro módulo, sólo valida y aplica el número que le llega.
    /// </summary>
    public void AddItem(
        QuotationItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();
        AddItemCore(
            itemId, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            updatedBy, occurredAt);
    }

    /// <summary>
    /// Elige el piso de escala global, o lo quita con <c>null</c>. Es una edición del
    /// encabezado: sube la versión y deja rastro de quién la hizo.
    ///
    /// Que el piso exista en algún producto lo comprueba el caso de uso, que es quien puede
    /// mirar el catálogo. Acá sólo se exige que sea un piso posible.
    /// </summary>
    public void SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();
        EnsureGlobalScaleFloorAllowed(floor, IsRetail);
        GlobalScaleFloor = floor;
        Touch(updatedBy, occurredAt);
    }

    /// <summary>
    /// Prende o apaga la cotización detal. Es una edición del encabezado, igual que
    /// <see cref="SetGlobalScaleFloor"/>: sube la versión y deja rastro de quién la hizo.
    /// </summary>
    public void SetIsRetail(bool isRetail, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();
        IsRetail = isRetail;

        // Prender detal limpia el piso y apagarlo no lo restaura: el piso es una elección del
        // asesor, no un estado derivado que el agregado pueda reconstruir. Guardarlo dormido
        // haría que apagar el detal aplicara sola una escala que nadie volvió a pedir, y dejaría
        // la respuesta publicando un piso que el recálculo está ignorando.
        if (isRetail)
        {
            GlobalScaleFloor = null;
        }

        Touch(updatedBy, occurredAt);
    }

    /// <summary>
    /// Fija la entrega por envío (spec 2026-10-17 flete §5.2). Valida contra la recogida y la moneda que la
    /// cotización ya tiene, así que el handler lo llama <b>después</b> de <see cref="UpdateDetails"/>; cuando
    /// la entrega pasa a recogida, le manda <see cref="ShippingDetails.None"/>. <see cref="UpdateDetails"/>
    /// no valida el envío a propósito: si lo hiciera, pasar a recogida con un envío guardado fallaría antes
    /// de que se pudiera limpiar.
    /// </summary>
    public void SetShipping(ShippingDetails shipping, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        EnsureEditable();
        AssignShipping(shipping.Validated(IsStorePickup, Currency, "quotation.quotation"));
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

    /// <summary>
    /// Las dos condiciones de un piso global, en un solo lugar: lo usan el mutador suelto y el
    /// guardado del encabezado (<see cref="UpdateDetails"/>), y si divergieran el guardado sería
    /// una puerta trasera a la exclusión con detal.
    ///
    /// <paramref name="isRetail"/> es el detal que va a quedar, no necesariamente el guardado: el
    /// mutador suelto pasa <see cref="IsRetail"/>, pero el guardado del encabezado trae el detal
    /// en el mismo cuerpo y lo aplica antes que el piso, así que el guard tiene que mirar ése.
    /// </summary>
    private static void EnsureGlobalScaleFloorAllowed(int? floor, bool isRetail)
    {
        // En detal ninguna línea recibe descuento, así que un piso guardado no describiría nada
        // de lo que la cotización está aplicando: se rechaza en vez de guardarse dormido. Va
        // antes de la validación de forma porque es la razón real del rechazo — decirle
        // "floor_invalid" a un piso perfectamente válido mandaría a mirar el select en vez del
        // interruptor de detal. Quitarlo (null) sigue permitido: no pide descuento, y
        // rechazarlo obligaría a apagar el detal para limpiar un piso que el detal ya limpió.
        if (isRetail && floor is not null)
        {
            throw new QuotationsDomainException(
                "quotation.retail.floor_not_allowed",
                "A retail quotation cannot use a global scale floor.");
        }

        EnsureGlobalScaleFloorIsPossible(floor);
    }

    // Catalog no deja crear una escala que arranque por debajo de 1, así que un piso de cero o
    // negativo no puede coincidir con ninguna: se corta acá y no llega al recálculo.
    private static void EnsureGlobalScaleFloorIsPossible(int? floor)
    {
        if (floor is { } value && value < 1)
        {
            throw new QuotationsDomainException(
                "quotation.global_scale.floor_invalid",
                "The global scale floor must be a positive unit count.");
        }
    }

    private void AddItemCore(
        QuotationItemId itemId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        // Una variante por cotizacion (spec 2026-10-20 §4; antes, un producto): dos lineas de la
        // misma variante son la misma linea con la cantidad partida, y partida ademas rompe el
        // descuento por escala (cada mitad resuelve su escala por separado y las dos pagan mas caro
        // que la suma junta). Quien quiera mas unidades cambia la cantidad de la linea que ya
        // existe. Dos tallas del mismo producto si son dos lineas. El codigo de error se conserva:
        // es contrato con el frontend.
        if (_items.Any(existing => existing.VariantId == variantId))
        {
            throw new QuotationsDomainException(
                "quotation.item.duplicate_product",
                "La variante ya está en la cotización.");
        }

        var item = QuotationItem.Create(
            itemId, Id, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            _items.Count + 1, Currency, PriceRounding, occurredAt);
        _items.Add(item);
        Touch(updatedBy, occurredAt);
    }

    /// <summary>Cambia la cantidad de una línea existente (US-4); el descuento y el impuesto,
    /// igual que en <see cref="AddItem"/>, ya vienen resueltos por la aplicación.</summary>
    public void UpdateItemQuantity(
        QuotationItemId itemId,
        decimal quantity,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        FindItem(itemId).UpdateQuantity(quantity, discountPercentage, taxPercentage, Currency, PriceRounding, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(QuotationItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();

        _items.Remove(FindItem(itemId));
        Touch(updatedBy, occurredAt);
    }

    /// <summary>Edita el encabezado (US-6, US-10): forma de pago, vigencia, notas y los datos de
    /// facturación/entrega. La tasa de impuesto no se edita acá — la trae
    /// cada línea desde su producto (RN-013). Reemplaza el recurso entero, mismo criterio que
    /// <c>Product.Update</c>: lo que no viene se limpia.</summary>
    public void UpdateDetails(
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        IReadOnlyDictionary<Guid, QuotationItemPricing>? repricing,
        bool isRetail,
        int? globalScaleFloor,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();
        // Detal antes que el piso, y contra el detal del cuerpo: así apagar el detal y elegir un
        // piso en el mismo guardado funciona, y prenderlo con un piso no nulo es un cuerpo
        // contradictorio que se rechaza con quotation.retail.floor_not_allowed — el mismo código
        // que el mutador suelto, sin regla cruzada en el validador de forma.
        EnsureGlobalScaleFloorAllowed(globalScaleFloor, isRetail);
        // Antes de asignar nada: un encabezado rechazado no puede quedar aplicado a medias.
        EnsureBillingIsConsistent(parties);

        ValidUntil = validUntil;
        // El guardado manda el encabezado entero, así que un piso ausente es "quitalo" y no "no
        // lo toques" — mismo criterio que ValidUntil o Notes. Lo mismo el detal: ausente es
        // false. Prender el detal deja el piso en null sin pasar por SetIsRetail porque el
        // guard de arriba ya garantizó que, con detal, el piso del cuerpo es null.
        IsRetail = isRetail;
        GlobalScaleFloor = globalScaleFloor;
        PaymentMethod = NormalizePaymentMethod(paymentMethod);
        Notes = NormalizeNotes(notes);
        Assign(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        IsStorePickup = parties.IsStorePickup;
        BillsToFinalConsumer = parties.BillsToFinalConsumer;
        PartyWithRetention = parties.Billing is null ? null : parties.BillingWithRetention;
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        ApplyBillingAccount(billingAccount, repricing, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    /// <summary>
    /// Cuál sería la moneda de esta cotización con esa cuenta de cobro. La aplicación la consulta
    /// **antes** de guardar, para saber si tiene que ir a buscar los precios de las líneas en otra
    /// moneda; el agregado la vuelve a calcular al aplicar, así que no depende de que el llamador
    /// haya preguntado.
    /// </summary>
    public string CurrencyFor(QuotationBillingAccount? billingAccount) =>
        billingAccount?.Currency ?? Currency;

    // Cambiar de cuenta puede cambiar la moneda, y con ella el precio de cada línea: los importes
    // guardados son el precio del producto en la moneda vieja, no una cifra convertible. Por eso
    // el repricing es obligatorio cuando hay líneas —lo trae la aplicación, resuelto contra el
    // catálogo— y no algo que este método pueda improvisar.
    private void ApplyBillingAccount(
        QuotationBillingAccount? billingAccount,
        IReadOnlyDictionary<Guid, QuotationItemPricing>? repricing,
        DateTimeOffset occurredAt)
    {
        BillingAccount = billingAccount?.Normalized();
        var currency = CurrencyFor(BillingAccount);
        if (currency == Currency)
        {
            return;
        }

        // La regla guardada es de la moneda vieja: se descarta y las líneas nacen con la de siempre de
        // la nueva. La del tenant para esa moneda la baja el recálculo (UsePriceRounding) a continuación.
        _priceRoundingDecimals = null;
        _priceRoundingMode = null;
        var rounding = PriceRounding.LegacyFor(currency);
        foreach (var item in _items)
        {
            if (repricing is null || !repricing.TryGetValue(item.VariantId, out var pricing))
            {
                throw new QuotationsDomainException(
                    "quotation.quotation.repricing_incomplete",
                    "Every line needs a price in the new currency before the quotation can change currency.");
            }

            item.Reprice(pricing, currency, rounding, occurredAt);
        }

        Currency = currency;

        // D-F15: el envío no tiene de dónde re-preciarse en la moneda nueva. Se limpian el valor y el modo
        // para que el asesor lo vuelva a escribir; la transportadora y la contraentrega no dependen de la
        // moneda y se conservan.
        FreightChargeMode = null;
        FreightAmount = 0m;
        FreightTaxPercentage = 0;
    }

    /// <summary>
    /// Cambia el cliente de la cotización mientras sigue editable (Draft o Sent).
    ///
    /// US-2 decía que el cliente no se cambiaba, y era razonable: la cotización copió datos suyos
    /// en varios lugares. Se habilita a pedido, y lo que arrastra está acá y no repartido en el
    /// caso de uso — las dos partes vuelven a "los datos del cliente" (las que había copiaron al
    /// cliente viejo: dejarlas sería facturarle a uno y entregarle a otro sin que nadie lo pidiera)
    /// y el snapshot de retención/excedente de IVA pasa a ser el del cliente nuevo, así que los
    /// totales cambian.
    ///
    /// Lo que **no** cambia: el número de cotización y sus líneas de producto. El número ya se
    /// emitió una vez y las líneas no dependen del cliente.
    /// </summary>
    public void ChangeClient(
        Guid clientId,
        bool customerWithRetention,
        bool customerVatSurplus,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        ClientId = EnsureValidClientId(clientId);
        Assign(QuotationParties.Empty);
        // El cliente nuevo puede no ser una empresa: la eleccion de nombre vuelve al default.
        BillingUsesBusinessName = false;
        // Facturar a consumidor final se decidió mirando al cliente anterior: el nuevo arranca
        // con la facturación por defecto, y con su retención/excedente de IVA aplicados.
        BillsToFinalConsumer = false;
        // Las partes se resetean a Empty arriba: sin fila de facturación propia, estos dos
        // vuelven a "sin contestar" — lo que responda el cliente nuevo lo trae CustomerWithRetention.
        PartyWithRetention = null;
        PartyVatSurplus = null;
        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        Touch(updatedBy, occurredAt);
    }

    /// <summary>US-12: marca la cotización como enviada. El PDF lo genera el backend y vive en
    /// `quotation_pdfs`, versionado contra <see cref="Version"/>: ya no es un archivo que el
    /// cliente elige y este agregado tenga que recordar. Desde <see cref="QuotationStatus.Draft"/> o
    /// desde <see cref="QuotationStatus.Sent"/>: reenviar es el mismo hecho para el agregado —
    /// PDF nuevo, <see cref="SentAt"/> nuevo, estado <see cref="QuotationStatus.Sent"/>—, y lo
    /// que lo distingue de un primer envío es la entrada de historial que escribe el caso de
    /// uso, no una transición distinta. Voided y Expired sí siguen sin volver para atrás a Sent.
    ///
    /// <see cref="SentAt"/> y <see cref="UpdatedAt"/> quedan en el mismo instante también en un
    /// reenvío, así que <see cref="HasChangesSinceSent"/> vuelve a <c>false</c>: lo que el
    /// cliente tiene en la mano es, otra vez, la versión vigente.
    ///
    /// <paramref name="products"/> es el catálogo de hoy para las líneas de esta cotización:
    /// enviar es salir del borrador, y desde acá cada línea conserva el código y el nombre que
    /// tenía su producto (owner, 2026-09-26). Un reenvío sólo completa las líneas que todavía no
    /// los tienen —las agregadas después del primer envío—; lo ya congelado no se pisa. Una línea
    /// cuyo producto no viene queda sin snapshot y se sigue leyendo en vivo: el envío nunca exigió
    /// que el producto exista, y no es acá donde se empieza a exigir.</summary>
    public void Send(
        MemberId sentBy,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<Guid, QuotationProductSnapshot> products)
    {
        EnsureSendable();

        CaptureProductSnapshots(products);
        SentAt = occurredAt;
        Status = QuotationStatus.Sent;
        UpdatedBy = sentBy;
        UpdatedAt = occurredAt;
        Version++;
    }

    // Por id de variante: la foto es la de la variante que vende la línea (spec 2026-10-20 §4).
    private void CaptureProductSnapshots(IReadOnlyDictionary<Guid, QuotationProductSnapshot> products)
    {
        foreach (var item in _items)
        {
            if (products.TryGetValue(item.VariantId, out var product))
            {
                item.CaptureProduct(product);
            }
        }
    }

    /// <summary>US-11: anula la cotización. Disponible desde Draft o Sent; queda de sólo
    /// lectura de ahí en más (<see cref="EnsureEditable"/> ya no deja pasar Voided).</summary>
    public void Void(MemberId voidedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();

        Status = QuotationStatus.Voided;
        UpdatedBy = voidedBy;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Precondiciones de <see cref="Send"/>, sin mutar nada — mismo criterio que
    /// <see cref="EnsureConvertibleToOrder"/>. Existe aparte para que el caso de uso pueda
    /// comprobarlas **antes** de firmar la URL del PDF y de entregarle el mensaje a WhatsApp:
    /// esos dos son efectos externos que no se deshacen, y una cotización que no puede pasar a
    /// Sent no puede haberle llegado al cliente.
    ///
    /// Sin vigencia la cotización nunca vence: <c>QuotationExpirationProcessor</c> filtra por
    /// <see cref="ValidUntil"/> no nulo, así que una Sent sin fecha quedaría convertible a pedido
    /// para siempre, con los precios congelados el día que se envió. Se exige acá **y también**
    /// en <see cref="EnsureConvertibleToOrder"/>, que desde que ésta dejó de exigir haber
    /// enviado (a pedido, 2026-09) ya no puede apoyarse en que toda cotización pasó por acá
    /// antes de convertirse.
    /// </summary>
    public void EnsureSendable()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_draft",
                "Only a draft or sent quotation can be marked as sent.");
        }

        if (ValidUntil is null)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.valid_until_required",
                "A quotation must have a validity date before it can be sent.");
        }

        // Una cotización que le llega al cliente facturada a otra persona sin su documento es una
        // que después no se puede facturar tal como se le mandó. Ver EnsureBillingIdentified.
        EnsureBillingIdentified();
    }

    /// <summary>US-16: convierte la cotización en pedido. Comprueba lo mismo que
    /// <see cref="EnsureConvertibleToOrder"/> y la deja en <see cref="QuotationStatus.Converted"/>,
    /// de sólo lectura desde ahí. El <see cref="Order"/> lo crea
    /// <c>ConvertQuotationToOrderHandler</c> justo después y en la misma unidad de trabajo: si
    /// guardar falla, no queda ni el pedido ni el cambio de estado. Mismo patrón que
    /// <see cref="Void"/> y <see cref="Expire"/>.</summary>
    public void ConvertToOrder(
        MemberId convertedBy,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<Guid, QuotationProductSnapshot> products)
    {
        EnsureConvertibleToOrder();

        // Un borrador se puede convertir sin haberse enviado, y también es salir del borrador: el
        // pedido nace con los productos congelados. Si ya se envió, lo congelado no se pisa.
        CaptureProductSnapshots(products);

        Status = QuotationStatus.Converted;
        UpdatedBy = convertedBy;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>US-16: valida que se pueda convertir en pedido, sin mutar nada. Ya no exige
    /// haberla enviado primero, ni que siga sin cambios desde ese envío (a pedido, 2026-09) —
    /// Draft o Sent alcanzan, mismo criterio de "sigue siendo un documento abierto" que
    /// <see cref="EnsureSendable"/>; no se convierte una anulada, una vencida ni una ya
    /// convertida. Quien cambia el estado es <see cref="ConvertToOrder"/>, que llama esto primero:
    /// existe aparte porque <see cref="CanBeConvertedToOrder"/> y la pantalla necesitan preguntar
    /// sin convertir.
    ///
    /// Que el cliente esté activo **no** se comprueba acá: este agregado no lo sabe, sólo tiene
    /// su id (referencia blanda). Lo revalida <c>ConvertQuotationToOrderHandler</c> contra
    /// <c>IQuotationCustomerLookup</c> justo antes de llamar a este método — mismo motivo que
    /// <see cref="QuotationCustomerEligibility"/> al crear.</summary>
    public void EnsureConvertibleToOrder()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.status_not_convertible",
                "Only a draft or sent quotation can be converted to an order.");
        }

        // Lo que un pedido necesita para existir y que la cotizacion puede no tener todavia. Se
        // comprueba aca y no en la pantalla porque es la condicion del negocio, no del formulario:
        // el pedido se crea desde este agregado y estos datos son los que hereda.
        //
        // La forma de pago NO esta en esta lista a proposito: el editor de cotizaciones dejo de
        // pedirla (queda `null` en todo lo creado desde entonces -- el formulario del frontend ya
        // no tiene campo que la escriba), asi que exigirla aca dejaba
        // inconvertible a cualquier cotizacion nueva sin que hubiera forma de arreglarlo desde la
        // pantalla. `Order` tampoco la usa para nada al crearse.
        if (_items.Count == 0)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.items_required",
                "A quotation without products cannot be converted to an order.");
        }

        if (ValidUntil is null)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.valid_until_required",
                "A quotation must have a validity date before it can be converted to an order.");
        }

        // Sin cuenta de cobro el pedido no sabe a donde se paga, que es justo lo que un pedido
        // tiene que decir.
        if (BillingAccount is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing account before it can be converted to an order.");
        }

        // Una parte con datos propios a medio llenar es la misma condicion que ya exige
        // EnsureBillingIsConsistent/Assign en otro momento (los seis campos o ninguno no se
        // fuerza al guardar, asi que puede llegar hasta aca incompleta): el pedido hereda lo que
        // haya en la parte, y una direccion sin ciudad o un telefono vacio no es algo que
        // Order pueda completar por su cuenta.
        if (Billing is { } billing && !billing.IsComplete)
        {
            throw new QuotationsDomainException(
                "quotation.billing.party_incomplete",
                "The billing party must have all its fields filled before converting to an order.");
        }

        if (Shipping is { } shipping && !shipping.IsComplete)
        {
            throw new QuotationsDomainException(
                "quotation.shipping.party_incomplete",
                "The shipping party must have all its fields filled before converting to an order.");
        }

        // El pedido es el paso que lleva a facturar, y sin el documento de a quién se le factura
        // no se puede (owner, 2026-09-26). Ver EnsureBillingIdentified.
        EnsureBillingIdentified();

        // Con datos propios de facturación nadie más dice si hay retención o excedente de IVA:
        // null es "todavía no se contestó", y un pedido no puede nacer con esa pregunta abierta
        // (a diferencia de con los datos del cliente, donde CustomerWithRetention/VatSurplus ya
        // la responden).
        if (Billing is not null && (PartyWithRetention is null || PartyVatSurplus is null))
        {
            throw new QuotationsDomainException(
                "quotation.billing.tax_profile_required",
                "The billing party must state whether it applies withholding tax and VAT surplus before converting to an order.");
        }
    }

    /// <summary>
    /// Si convertir en pedido es posible ahora, salvo por si el cliente sigue activo (este
    /// agregado no lo sabe — ver <see cref="EnsureConvertibleToOrder"/>). Misma regla que ese
    /// método, en forma de pregunta: la pantalla la usa para saber qué decir en vez de
    /// reimplementar las condiciones, y el handler la revalida al convertir de verdad.
    /// </summary>
    public bool CanBeConvertedToOrder =>
        Status is QuotationStatus.Draft or QuotationStatus.Sent
        && _items.Count > 0
        && ValidUntil is not null
        && BillingAccount is not null
        && (Billing is not { } billing || billing.IsComplete)
        && (Shipping is not { } shipping || shipping.IsComplete)
        && !BillingLacksIdentification
        && (Billing is null || (PartyWithRetention is not null && PartyVatSurplus is not null));

    /// <summary>
    /// Vuelve a tomar del cliente maestro <see cref="CustomerWithRetention"/> y
    /// <see cref="CustomerVatSurplus"/> mientras la cotización sigue editable (Draft o Sent).
    /// Silencioso a propósito (no valida ni lanza) para poder llamarse también desde una
    /// lectura: una vez Voided, Expired o Converted la cotización queda tal cual quedó, sin
    /// excepción, y nada la vuelve a tocar. En Converted eso importa doble: sus totales son los
    /// que heredó el pedido.
    ///
    /// **No pasa por <c>Touch</c>, y eso es carga estructural, no un olvido:** mover
    /// <see cref="UpdatedAt"/> acá dejaría <see cref="HasChangesSinceSent"/> en <c>true</c> por
    /// el solo hecho de abrir la cotización después de que su cliente cambió de perfil fiscal,
    /// y el gate de <see cref="EnsureConvertibleToOrder"/> pediría reenviarla sin que nadie la
    /// haya editado.
    /// </summary>
    public void RefreshCustomerTaxProfile(bool customerWithRetention, bool customerVatSurplus)
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent)) return;
        if (CustomerWithRetention == customerWithRetention &&
            CustomerVatSurplus == customerVatSurplus)
        {
            return;
        }

        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        RecalculateTotals();
    }

    /// <summary>
    /// Reescribe el descuento de varias líneas de una sola vez y rehace los totales.
    ///
    /// Existe porque desde la agrupación de escalas y la compuerta de compra mínima el descuento
    /// de una línea **depende de las otras**: la aplicación resuelve la cotización entera contra
    /// el catálogo (<c>QuotationScaleGroupPricing</c>) y baja acá el resultado ya calculado. El
    /// dominio no sabe de escalas de otro módulo — mismo criterio que <see cref="AddItem"/>.
    ///
    /// **No es una edición.** No toca <see cref="Version"/>, <see cref="UpdatedBy"/> ni
    /// <see cref="UpdatedAt"/> del encabezado: corre justo después de la mutación que sí lo hizo,
    /// y volver a tocarlos contaría dos veces el mismo cambio. Por lo mismo **no** pasa por
    /// <see cref="EnsureEditable"/>: la cotización de un pedido ya convertido también se
    /// recalcula cuando se editan sus líneas, y ahí <see cref="EnsureEditable"/> lanzaría.
    ///
    /// Una línea que no venga en <paramref name="discounts"/> se queda como está. El recálculo
    /// manda lo que resolvió, y lo que no menciona no se toca — así un llamador que sólo conoce
    /// parte de las líneas no le borra el descuento al resto.
    /// </summary>
    public void ApplyGroupDiscounts(
        IReadOnlyDictionary<QuotationItemId, QuotationItemDiscount> discounts,
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

    /// <summary>US-19: vencimiento automático. Sólo un job programado la llama —qué cotizaciones
    /// calificar (Sent con <see cref="ValidUntil"/> ya pasado) es su criterio de selección, no
    /// una regla que este método vuelva a comprobar—, así que no hay <see cref="MemberId"/>: no
    /// hay una persona detrás de la transición. <see cref="UpdatedBy"/> no se toca — sigue
    /// mostrando el último editor humano, que es más informativo que vaciarlo.</summary>
    public void Expire(DateTimeOffset occurredAt)
    {
        if (Status != QuotationStatus.Sent)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_sent",
                "Only a sent quotation can expire.");
        }

        Status = QuotationStatus.Expired;
        UpdatedAt = occurredAt;
        Version++;
    }

    // US-10: "se puede editar en Draft y Sent... se bloquea una vez convertida a pedido". Bloquea
    // todo lo que no sea Draft o Sent: Converted (US-16), Voided (US-11: "quedan de sólo
    // lectura") y Expired. Void pasa por acá, así que una convertida tampoco se anula.
    private void EnsureEditable()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_editable",
                "The quotation cannot be edited in its current status.");
        }
    }

    // Consumidor final tiene nombre y NIT fijos (FinalConsumer): una parte de facturación propia
    // o la razón social del cliente al lado dirían otro nombre para la misma factura.
    private static void EnsureBillingIsConsistent(QuotationParties parties)
    {
        if (parties.BillsToFinalConsumer &&
            (parties.Billing is not null || parties.BillingUsesBusinessName))
        {
            throw new QuotationsDomainException(
                "quotation.billing.final_consumer_conflict",
                "A quotation billed to the final consumer cannot carry its own billing party or bill to the customer's business name.");
        }
    }

    /// <summary>
    /// Una facturación con nombre propio es otra persona, y sin su documento no se puede facturar
    /// (owner, 2026-09-26). Sin nombre propio la factura sigue saliendo a nombre del cliente, con
    /// el documento de su ficha, así que ahí no se pide.
    ///
    /// <b>No lo llama <see cref="UpdateDetails"/></b>, a propósito: el cálculo previo aplica el
    /// mismo cuerpo mientras la persona todavía escribe —puso el nombre y no el número—, y un 422
    /// en cada tecla no le sirve a nadie. Lo llaman quienes sí escriben o avanzan: la creación, el
    /// guardado (<c>SaveQuotationHandler</c>, antes de persistir), <see cref="EnsureSendable"/> y
    /// <see cref="EnsureConvertibleToOrder"/>. Las dos transiciones además atajan las filas
    /// guardadas antes de la columna, que tienen nombre y no número: se leen igual, pero no avanzan.
    /// </summary>
    public void EnsureBillingIdentified()
    {
        if (BillingLacksIdentification)
        {
            throw new QuotationsDomainException(
                "quotation.billing.identification_required",
                "A billing party with its own name requires an identification number.");
        }
    }

    private bool BillingLacksIdentification =>
        Billing is { } billing
        && !string.IsNullOrWhiteSpace(billing.Name)
        && (string.IsNullOrWhiteSpace(billing.IdentificationNumber) || billing.IdentificationType is null);

    // Reemplaza las dos partes siempre, incluidas las ausentes: `UpdateDetails` reemplaza el
    // recurso entero, así que una parte que llega null borra la fila que hubiera -- que es
    // exactamente "volvé a usar los datos del cliente" (el switch prendido de nuevo).
    //
    // Recoger en tienda gana sobre la parte de entrega: se asigna null aunque el request la
    // traiga, y eso borra la fila que hubiera (ver IsStorePickup).
    private void Assign(QuotationParties parties)
    {
        Assign(QuotationPartyRole.Billing, parties.Billing);
        Assign(QuotationPartyRole.Shipping, parties.IsStorePickup ? null : parties.Shipping);
    }

    private void Assign(QuotationPartyRole role, QuotationPartyDetails? details)
    {
        var existing = FindParty(role);

        if (details is null)
        {
            if (existing is not null)
            {
                _parties.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            _parties.Add(QuotationParty.Create(Id, role, details));
            return;
        }

        existing.Apply(details);
    }

    private QuotationParty? FindParty(QuotationPartyRole role) =>
        _parties.FirstOrDefault(party => party.Role == role);

    private QuotationItem FindItem(QuotationItemId itemId) =>
        _items.FirstOrDefault(item => item.Id == itemId)
            ?? throw new QuotationsDomainException(
                "quotation.item.not_found",
                "The quotation item was not found.");

    private void Touch(MemberId updatedBy, DateTimeOffset occurredAt)
    {
        RecalculateTotals();
        UpdatedBy = updatedBy;
        UpdatedAt = occurredAt;
        Version++;
    }

    // La aritmética vive en DocumentTotals (spec 2026-10-15 §4.3): el pedido suma con la misma
    // regla, y un cambio de redondeo cambia los dos documentos a la vez.
    private void RecalculateTotals()
    {
        var totals = DocumentTotals.Compute(
            _items.Select(item => new LineAmountsView(item.Subtotal, item.DiscountAmount, item.TaxAmount)),
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

    private static string NormalizeQuotationNumber(string quotationNumber)
    {
        if (string.IsNullOrWhiteSpace(quotationNumber))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.number_required",
                "The quotation number is required.");
        }

        var trimmed = quotationNumber.Trim();
        return trimmed.Length > QuotationNumberMaxLength
            ? throw new QuotationsDomainException(
                "quotation.quotation.number_too_long",
                $"The quotation number cannot exceed {QuotationNumberMaxLength} characters.")
            : trimmed;
    }

    private static Guid EnsureValidClientId(Guid clientId) =>
        clientId == Guid.Empty
            ? throw new QuotationsDomainException(
                "quotation.quotation.client_required",
                "The quotation client is required.")
            : clientId;

    private static string? NormalizePaymentMethod(string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return null;
        }

        var trimmed = paymentMethod.Trim();
        return trimmed.Length > PaymentMethodMaxLength
            ? throw new QuotationsDomainException(
                "quotation.quotation.payment_method_too_long",
                $"The payment method cannot exceed {PaymentMethodMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizeNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}
