using BuildingBlocks.Domain.Catalog;
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

    /// <summary>Como <c>Order.ExternalKeyMaxLength</c>: la clave de idempotencia de un borrador creado sin sesión.</summary>
    public const int ExternalKeyMaxLength = 128;

    private readonly List<QuotationItem> _items = [];

    private readonly List<QuotationParty> _parties = [];

    private readonly List<DocumentWithholding> _withholdings = [];

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
        bool customerVatSurplus,
        IReadOnlyList<WithholdingRate> customerWithholdings,
        MunicipalContext municipal,
        MemberId createdBy,
        DateTimeOffset occurredAt,
        QuotationKind kind)
    {
        ArgumentNullException.ThrowIfNull(customerWithholdings);
        ArgumentNullException.ThrowIfNull(municipal);
        Id = id;
        TenantId = tenantId;
        Kind = kind;
        if (kind == QuotationKind.Project)
        {
            AssignProjectScope(Domain.ProjectScope.Empty);
        }

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
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        BillingAccount = billingAccount?.Normalized();
        // The billing account rules when there is one; otherwise the tenant default, read and
        // normalised against the catalogue by CreateQuotationHandler (this assembly cannot see it). Una empresa sin
        // cuenta (spec 2026-11-17, R3) no fija la moneda: elegir una cuenta es la forma de cambiarla.
        Currency = BillingAccount is { HasBankAccount: true } account ? account.Currency : defaultCurrency;
        CustomerVatSurplus = customerVatSurplus;
        // Spec ReteICA por empresa §7.6, «En la creación»: el municipio sale del mismo cuerpo (P13); en un país sin tipos
        // municipales no hay (A7).
        OperationCityId = MunicipalReResolver.HasMunicipalTypes(BillingAccount?.Country) ? municipal.OperationCityId : null;
        // La foto del cliente: sus tasas nacionales, de todos sus países (D16), más la municipal por la regla del cliente.
        // Los selectores de facturación propia nacen vacíos.
        ReplaceWithholdings(
            WithholdingSource.Customer,
            MunicipalReResolver.ByCustomerRule(customerWithholdings, municipal, OperationCityId));
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

    /// <summary>El tipo de documento (spec 2026-11-26 §5). Se fija al crear y ningún método lo cambia; el pedido lo
    /// copia.</summary>
    public QuotationKind Kind { get; private set; }

    /// <summary>El alcance y el precio de un proyecto; <c>null</c> en una cotización <see cref="QuotationKind.Standard"/>.
    /// Lo cambia <see cref="ReplaceProjectScope"/>, que mantiene la única línea del documento.</summary>
    public ProjectScope? ProjectScope { get; private set; }

    /// <summary>Espejo de <see cref="Domain.ProjectScope.PricingMode"/>: la fila del listado lo lee sin abrir el
    /// <c>jsonb</c>.</summary>
    public ProjectPricingMode? ProjectPricingMode { get; private set; }

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

    /// <summary>Copia de <c>Customer.VatSurplus</c>, congelada al crear la cotización como las tasas del
    /// cliente (spec impuestos por tipo D24): editar al cliente después no la mueve. Sólo la recopia
    /// <see cref="ChangeClient"/>. La usa <see cref="RecalculateTotals"/> para eximir de IVA a esta
    /// cotización entera.</summary>
    public bool CustomerVatSurplus { get; private set; }

    /// <summary>La suma de las retenciones aplicadas (<see cref="AppliedWithholdings"/>), 0 si no aplica
    /// ninguna. Informativo hacia <see cref="Total"/> (que sigue siendo lo facturado) pero resta hacia
    /// <see cref="NetTotal"/> — es lo que el cliente le retiene al vendedor, no algo que la cotización deje
    /// de facturar.</summary>
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

    /// <summary>
    /// Spec 2026-10-29 §8.3 y D-A16: la clave de idempotencia del borrador que crea una integración sin sesión (hoy, un agente IA:
    /// <c>ai:{jobId}:{callId}</c>). <c>null</c> en toda cotización creada por una persona. El índice único parcial
    /// <c>IX_quotations_tenant_external_key</c> es lo que deja una sola cotización si el trabajo se repite.
    /// </summary>
    public string? ExternalKey { get; private set; }

    public IReadOnlyCollection<QuotationItem> Items => _items;

    /// <summary>
    /// Si la cotización vende algo que se despacha (spec 2026-11-18 §5): alguna línea <c>Physical</c>, o ninguna línea,
    /// que es el estado de hoy y el del formulario vacío. Derivado, sin columna: el listado lo pregunta con un
    /// <c>EXISTS</c> (<c>IQuotationRepository.FindIdsRequiringShippingAsync</c>), y las dos reglas son la misma. El
    /// backend nunca pone ni borra el envío por esto; sólo se lo dice a la pantalla. Un proyecto nunca se despacha,
    /// tampoco sin líneas (spec 2026-11-26 §6.4).
    /// </summary>
    public bool RequiresShipping =>
        Kind != QuotationKind.Project
        && (_items.Count == 0 || _items.Exists(item => item.Kind == ProductKind.Physical));

    /// <summary>A quién se le factura y a quién se le entrega, **sólo cuando no son los datos del
    /// cliente** (US-6): una parte ausente es "usá los del cliente". Ver
    /// <see cref="QuotationParty"/>.</summary>
    public IReadOnlyCollection<QuotationParty> Parties => _parties;

    /// <summary>Con qué empresa y a qué cuenta se cobra esta cotización, congelado al guardarla
    /// (<see cref="QuotationBillingAccount"/>). Null mientras nadie la eligió.</summary>
    public QuotationBillingAccount? BillingAccount { get; private set; }

    /// <summary>
    /// El municipio de la operación (spec ReteICA por empresa §7.5): decide qué tasa municipal aplica. Arranca en el de la
    /// empresa (A6) y el asesor lo puede cambiar; null sin empresa, en un país sin tipos municipales (A7) y en un documento
    /// anterior a este trabajo hasta que alguien lo fije (A8). Sin FK, como el municipio de las partes.
    /// </summary>
    public Guid? OperationCityId { get; private set; }

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
        if (ProjectScope is { } scope)
        {
            // La línea es la suma de las fases ya redondeadas (P8): redondear sólo la línea guardada la separaría de lo
            // que dice el alcance.
            AssignProjectScope(scope.RoundedWith(rounding));
            foreach (var item in _items)
            {
                item.ChangeUnitPrice(ProjectScope!.Total ?? 0m, ChargesVat, Currency, rounding, occurredAt);
            }
        }
        else
        {
            foreach (var item in _items)
            {
                item.ApplyDiscount(item.DiscountPercentage, item.DiscountOrigin, Currency, rounding, occurredAt);
            }
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
    /// Apaga las retenciones y el excedente de IVA (<see cref="AppliedWithholdings"/>,
    /// <see cref="AppliesVatSurplus"/>) sin tocar la foto de tasas ni <see cref="CustomerVatSurplus"/>:
    /// desmarcarlo los restituye sin volver a consultar al cliente.
    /// </summary>
    public bool BillsToFinalConsumer { get; private set; }

    /// <summary>Si quien recibe la factura tiene excedente de IVA, cuando <see cref="Billing"/> trae datos
    /// propios — nadie más lo sabe con datos propios, así que se pregunta al armar la cotización
    /// (<see cref="QuotationParties.BillingVatSurplus"/>). Null mientras <see cref="Billing"/> sigue los del
    /// cliente (ahí manda <see cref="CustomerVatSurplus"/>) o mientras no se contestó.</summary>
    public bool? PartyVatSurplus { get; private set; }

    /// <summary>
    /// La foto de las tasas de retención (spec impuestos por tipo §7.5, D17): las del cliente
    /// (<see cref="WithholdingSource.Customer"/>, copiadas al crear y sólo recopiadas por
    /// <see cref="ChangeClient"/>) y las de los selectores de facturación propia
    /// (<see cref="WithholdingSource.Billing"/>, <see cref="SetBillingWithholdings"/>). Las dos fuentes
    /// conviven: pasar de un modo de facturación al otro no borra la del otro modo. Cada fila guarda lo que
    /// dio el último recálculo.
    /// </summary>
    public IReadOnlyCollection<DocumentWithholding> Withholdings => _withholdings;

    /// <summary>Las tasas del cliente, en el orden en que llegaron.</summary>
    public IEnumerable<DocumentWithholding> CustomerWithholdings => WithholdingsOf(WithholdingSource.Customer);

    /// <summary>Lo elegido en los selectores de facturación propia, en el orden en que llegó.</summary>
    public IEnumerable<DocumentWithholding> BillingWithholdings => WithholdingsOf(WithholdingSource.Billing);

    /// <summary>Las tasas de la fuente vigente, apliquen o no: los selectores con parte de facturación propia, las
    /// del cliente si no. Es lo que hereda el pedido al convertir.</summary>
    public IEnumerable<DocumentWithholding> ActiveWithholdings => WithholdingsOf(ActiveWithholdingSource);

    /// <summary>Las retenciones que aplicaron en el último recálculo, de la fuente vigente.</summary>
    public IEnumerable<DocumentWithholding> AppliedWithholdings =>
        ActiveWithholdings.Where(withholding => withholding.Applied);

    /// <summary>Por qué cada tasa de la fuente vigente aplica o no, en su orden (spec retenciones por UVT §5.1); las de
    /// otro país no salen.</summary>
    public IReadOnlyList<WithholdingVerdict> WithholdingVerdicts =>
        DocumentWithholding.Explain(
            [.. ActiveWithholdings], BillsToFinalConsumer, BillingAccount, Currency, OperationCityId,
            WithholdingBases.From(LineAmounts));

    /// <summary>Lo que cada línea aporta a los totales y a las bases de retención, con su tipo (spec 2026-11-18 §4.2).</summary>
    private IEnumerable<LineAmountsView> LineAmounts =>
        _items.Select(item => new LineAmountsView(item.Subtotal, item.DiscountAmount, item.TaxAmount, item.Kind));

    private WithholdingSource ActiveWithholdingSource =>
        Billing is not null ? WithholdingSource.Billing : WithholdingSource.Customer;

    /// <summary>Si el excedente de IVA exime a esta cotización: con datos propios de facturación manda
    /// <see cref="PartyVatSurplus"/> (false mientras no se conteste), con los del cliente
    /// <see cref="CustomerVatSurplus"/>; nunca a consumidor final. Es lo que usa
    /// <see cref="RecalculateTotals"/> y lo que tiene que leer quien imprima "exento".</summary>
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
        bool customerVatSurplus,
        IReadOnlyList<WithholdingRate> customerWithholdings,
        MunicipalContext municipal,
        MemberId createdBy,
        DateTimeOffset occurredAt,
        QuotationKind kind = QuotationKind.Standard) =>
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
            customerVatSurplus,
            customerWithholdings,
            municipal,
            createdBy,
            occurredAt,
            kind);

    /// <summary>
    /// El municipio de la operación de una cotización que se crea (owner 2026-11-14, plan P13): no hay municipio guardado
    /// que conservar, así que ausente y <c>null</c> son el de la empresa; explícito, el elegido; sin empresa o en un país
    /// sin tipos municipales, ninguno (A7). La aplicación lo calcula antes de consultar las ofertas.
    /// </summary>
    public static Guid? OperationCityAtCreation(QuotationBillingAccount? billingAccount, OperationCityChoice choice) =>
        choice.ForNewCompany(billingAccount);

    /// <summary>
    /// El municipio de la operación que quedaría con esa cuenta y esa elección (spec §7.5, A6): si cambia la empresa, el
    /// de la empresa nueva salvo que venga uno explícito; con la misma empresa, ausente conserva el guardado, <c>null</c>
    /// es el de la empresa y explícito el elegido. Sin empresa o en un país sin tipos municipales, ninguno (A7). La
    /// aplicación lo calcula antes de consultar las ofertas (plan P4).
    /// </summary>
    public Guid? OperationCityFor(QuotationBillingAccount? billingAccount, OperationCityChoice choice) =>
        choice.For(billingAccount, BillingAccount?.CompanyId, OperationCityId);

    /// <summary>
    /// Agrega una línea (US-3). <paramref name="discountPercentage"/> ya viene resuelto por la
    /// aplicación contra la escala de precios del producto para <paramref name="quantity"/>
    /// (<c>IQuotationProductPricingLookup</c> + el resolver de escala) — el dominio no sabe nada
    /// de escalas de otro módulo, sólo valida y aplica el número que le llega.
    ///
    /// <paramref name="kind"/> y <paramref name="salesUnit"/> son los del producto en el catálogo al agregar la línea
    /// (spec 2026-11-18 §3.4): la línea los fija ahora y no los vuelve a leer. Con default porque el valor de hoy es
    /// <c>Physical</c>/<c>Unit</c>; todo camino de Application que lee la variante los pasa.
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
        DateTimeOffset occurredAt,
        ProductKind kind = ProductKind.Physical,
        SalesUnit salesUnit = SalesUnit.Unit)
    {
        EnsureEditable();
        EnsureManualLines();
        AddItemCore(
            itemId, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage, kind, salesUnit,
            updatedBy, occurredAt);
    }

    /// <summary>
    /// Reemplaza el alcance entero de un proyecto y deja la única línea que lo cobra (spec 2026-11-26 §5 y §6.4): la del
    /// servicio elegido, cantidad 1, precio igual al total y sin descuento. Sin servicio no hay línea; sin total, vale 0.
    /// Cada precio se redondea con la regla del documento, así que la aplicación baja antes la vigente
    /// (<see cref="UsePriceRounding"/>).
    ///
    /// <paramref name="service"/> es lo que dice el catálogo del servicio y sólo hace falta cuando
    /// <see cref="Domain.ProjectScope.ServiceVariantId"/> cambia: la línea que ya existe conserva su foto y sólo cambia de
    /// precio. Faltar ahí es un error del llamador, no del usuario.
    ///
    /// Si no cambia nada, no toca la versión.
    /// </summary>
    public void ReplaceProjectScope(
        ProjectScope scope, ProjectServiceLine? service, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureEditable();
        if (ProjectScope is not { } current)
        {
            throw new InvalidOperationException("Only a project quotation has a project scope.");
        }

        var rounded = scope.RoundedWith(PriceRounding);
        var serviceVariantId = rounded.ServiceVariantId;
        var line = _items.SingleOrDefault();
        var replacesLine = serviceVariantId is not null && line?.VariantId != serviceVariantId;
        // Antes de asignar nada: un alcance rechazado no puede quedar aplicado a medias.
        if ((serviceVariantId != current.ServiceVariantId || replacesLine) && service?.VariantId != serviceVariantId)
        {
            throw new InvalidOperationException(
                "Changing the project service needs the line of the new variant.");
        }

        var changed = rounded != current;
        AssignProjectScope(rounded);
        var total = rounded.Total ?? 0m;
        if (replacesLine)
        {
            _items.Clear();
            // AddItemCore ya toca el documento: es la misma edición, así que no se vuelve a tocar.
            AddItemCore(
                QuotationItemId.New(), service!.VariantId, service.ProductId, 1m, total, 0m, service.TaxPercentage,
                service.Kind, service.SalesUnit, updatedBy, occurredAt);
            return;
        }

        if (serviceVariantId is null && line is not null)
        {
            _items.Clear();
            changed = true;
        }
        else if (line is not null && line.UnitPrice != total)
        {
            line.ChangeUnitPrice(total, ChargesVat, Currency, PriceRounding, occurredAt);
            changed = true;
        }

        if (changed)
        {
            Touch(updatedBy, occurredAt);
        }
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
        // Una empresa sin IVA no cobra IVA en el envío (spec 2026-11-17 §5.2), venga lo que venga.
        FreightTaxPercentage = ChargesVat ? shipping.FreightTaxPercentage : 0;
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
        ProductKind kind,
        SalesUnit salesUnit,
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

        // taxPercentage es la tasa del producto; la línea aplica el IVA de la empresa (spec 2026-11-17, R1).
        var item = QuotationItem.Create(
            itemId, Id, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage, ChargesVat,
            _items.Count + 1, Currency, PriceRounding, kind, salesUnit, occurredAt);
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
        EnsureManualLines();

        FindItem(itemId).UpdateQuantity(
            quantity, discountPercentage, taxPercentage, ChargesVat, Currency, PriceRounding, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    public void RemoveItem(QuotationItemId itemId, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();
        EnsureManualLines();

        _items.Remove(FindItem(itemId));
        Touch(updatedBy, occurredAt);
    }

    /// <summary>Quita las líneas cuya variante se borró del catálogo. La usa el borrado de un
    /// producto (spec 2026-11-08, paso 3). Sólo aplica a un borrador. No es una edición del
    /// asesor, por eso no cambia <see cref="UpdatedBy"/>; sí sube la versión para que un guardado
    /// con la versión vieja falle. Devuelve cuántas líneas quitó; si no quitó ninguna, no toca nada.</summary>
    public int RemoveItemsOfDeletedVariants(IReadOnlySet<Guid> variantIds, DateTimeOffset occurredAt)
    {
        if (Status is not QuotationStatus.Draft)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_draft",
                "Only a draft quotation can lose lines of deleted variants.");
        }

        var removed = _items.RemoveAll(item => variantIds.Contains(item.VariantId));
        if (removed == 0)
        {
            return 0;
        }

        RecalculateTotals();
        UpdatedAt = occurredAt;
        Version++;
        return removed;
    }

    /// <summary>Edita el encabezado (US-6, US-10): forma de pago, vigencia, notas y los datos de
    /// facturación/entrega. La tasa de impuesto no se edita acá — la trae
    /// cada línea desde su producto (RN-013). Reemplaza el recurso entero, mismo criterio que
    /// <c>Product.Update</c>: lo que no viene se limpia.
    ///
    /// <paramref name="municipal"/> trae el municipio de la operación resultante (<see cref="OperationCityFor"/>) y lo que
    /// se ofrece ahí (spec ReteICA por empresa §7.6). Cambiar la empresa o el municipio re-resuelve las filas municipales
    /// de las dos fuentes: la del cliente con la regla del cliente, la de facturación propia moviendo lo elegido (plan
    /// P5). Reenviar la misma empresa completa su municipio en la cuenta sin re-resolver. Devuelve lo que pasó, para el
    /// historial.
    ///
    /// <paramref name="freightTaxPercentageWithVat"/> es la tasa del envío del tenant, y sólo hace falta cuando
    /// <see cref="NeedsFreightTaxRateFor"/> dice que sí: pasar a una empresa con IVA con un envío cobrado en COP vuelve a
    /// gravarlo (spec 2026-11-17, R2). Faltar ahí es un error del llamador, no del usuario.</summary>
    public MunicipalReResolution UpdateDetails(
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationParties parties,
        QuotationBillingAccount? billingAccount,
        IReadOnlyDictionary<Guid, QuotationItemPricing>? repricing,
        bool isRetail,
        int? globalScaleFloor,
        MunicipalContext municipal,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        int? freightTaxPercentageWithVat = null)
    {
        ArgumentNullException.ThrowIfNull(municipal);
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
        PartyVatSurplus = parties.Billing is null ? null : parties.BillingVatSurplus;
        // Las retenciones nacionales no se tocan acá: volver a los datos del cliente deja guardados los selectores
        // (spec §7.5, «no borra la foto del otro modo»), y los selectores los fija SetBillingWithholdings.
        var previousCompanyId = BillingAccount?.CompanyId;
        ApplyBillingAccount(billingAccount, repricing, freightTaxPercentageWithVat, occurredAt);
        var result = ReResolveMunicipal(previousCompanyId, municipal);
        Touch(updatedBy, occurredAt);
        return result;
    }

    // Spec §7.6, disparadores 1 y 2: otra empresa (no otra cuenta de la misma) u otro municipio, incluido de null a uno.
    private MunicipalReResolution ReResolveMunicipal(Guid? previousCompanyId, MunicipalContext municipal)
    {
        var cityAfter = MunicipalReResolver.HasMunicipalTypes(BillingAccount?.Country) ? municipal.OperationCityId : null;
        if (BillingAccount?.CompanyId == previousCompanyId && cityAfter == OperationCityId)
        {
            return MunicipalReResolution.None;
        }

        var cityBefore = OperationCityId;
        OperationCityId = cityAfter;
        var customer = MunicipalReResolver.ReResolve(
            [.. CustomerWithholdings.Select(row => row.Rate)], municipal, cityAfter, customerRule: true);
        var billing = MunicipalReResolver.ReResolve(
            [.. BillingWithholdings.Select(row => row.Rate)], municipal, cityAfter, customerRule: false);
        ReplaceWithholdings(WithholdingSource.Customer, customer.Rates);
        ReplaceWithholdings(WithholdingSource.Billing, billing.Rates);
        // En la cotización la fuente del cliente no tiene aplicaciones a mano: sólo el pedido las informa (plan P5).
        return new MunicipalReResolution(
            true, cityBefore, cityAfter, [.. customer.Discarded, .. billing.Discarded], []);
    }

    /// <summary>
    /// Fija lo elegido en los selectores de retención de la facturación propia (spec §7.5, D23), ya resuelto
    /// contra el catálogo por la aplicación (<c>WithholdingSelection</c>, plan P11). El agregado vuelve a
    /// exigir lo que guarda: parte de facturación propia, empresa facturadora, cada tasa de su país y una por
    /// tipo.
    ///
    /// Una lista vacía siempre se acepta y vacía los selectores (ruling C9): un borrador con facturación
    /// propia se puede guardar antes de elegir la empresa, y sin empresa no hay selectores.
    ///
    /// Devuelve si cambió algo; si no, no toca la versión.
    /// </summary>
    public bool SetBillingWithholdings(
        IReadOnlyList<WithholdingRate> rates,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(rates);
        EnsureEditable();
        if (rates.Count > 0)
        {
            EnsureSelectableWithholdings(rates);
        }

        if (BillingWithholdings.Select(withholding => withholding.Rate).SequenceEqual(rates))
        {
            return false;
        }

        ReplaceWithholdings(WithholdingSource.Billing, rates);
        Touch(updatedBy, occurredAt);
        return true;
    }

    // Sin parte propia no hay selectores; lo demás (país de la empresa, uno por tipo) es la regla compartida con el
    // pedido.
    private void EnsureSelectableWithholdings(IReadOnlyList<WithholdingRate> rates) =>
        WithholdingSelectorRules.Ensure(
            rates, Billing is null ? null : BillingAccount?.Country, OperationCityId, "quotation.billing");

    /// <summary>
    /// Cuál sería la moneda de esta cotización con esa cuenta de cobro. La aplicación la consulta
    /// **antes** de guardar, para saber si tiene que ir a buscar los precios de las líneas en otra
    /// moneda; el agregado la vuelve a calcular al aplicar, así que no depende de que el llamador
    /// haya preguntado.
    /// </summary>
    public string CurrencyFor(QuotationBillingAccount? billingAccount) =>
        billingAccount is { HasBankAccount: true } ? billingAccount.Currency : Currency;

    /// <summary>
    /// Si lo que emite esta cotización lleva IVA (spec 2026-11-17 §5.2): el de la empresa congelado con la cuenta. Sin
    /// empresa todavía calcula con IVA, como hasta ahora, y se recalcula al elegirla.
    /// </summary>
    public bool ChargesVat => BillingAccount?.ChargesVat ?? true;

    // Enviar y convertir exigen la empresa emisora, no la cuenta (spec 2026-11-17 §4.2): una empresa sin cuenta emite
    // igual, con «Pago: por definir».
    private bool HasBillingCompany => BillingAccount is not null;

    /// <summary>
    /// Si guardar esa cuenta vuelve a gravar el envío (spec 2026-11-17, R2): hoy sin IVA, con IVA después, con un envío
    /// cobrado con valor y en una moneda que discrimina el IVA, sin que la cuenta cambie la moneda (ahí el envío se
    /// limpia). Si dice que sí, el handler lee la tasa del envío del tenant —que puede faltar:
    /// <c>quotation.quotation.freight_tax_rate_missing</c>— y la pasa a <see cref="UpdateDetails"/>.
    /// </summary>
    public bool NeedsFreightTaxRateFor(QuotationBillingAccount? billingAccount) =>
        !ChargesVat
        && (FrozenVat(billingAccount)?.ChargesVat ?? true)
        && FreightChargeMode == Domain.FreightChargeMode.Charged
        && FreightAmount > 0
        && CurrencyFor(billingAccount) == Currency
        && QuotationItem.ItemizesTaxIn(Currency);

    // El IVA es la foto de la empresa al elegirla (spec 2026-11-17 §5.2): reenviar la misma empresa —el guardado manda el
    // encabezado entero— no lo relee aunque la empresa lo haya cambiado después. Sólo otra empresa trae el suyo.
    private QuotationBillingAccount? FrozenVat(QuotationBillingAccount? account) =>
        account is not null && BillingAccount is { } current && current.CompanyId == account.CompanyId
            ? account with { ChargesVat = current.ChargesVat }
            : account;

    // R2: cambió el IVA de la empresa emisora. Cada línea se rehace desde la tasa del producto y el envío sigue al
    // documento; los totales los rehace Touch.
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

    // Cambiar de cuenta puede cambiar la moneda, y con ella el precio de cada línea: los importes
    // guardados son el precio del producto en la moneda vieja, no una cifra convertible. Por eso
    // el repricing es obligatorio cuando hay líneas —lo trae la aplicación, resuelto contra el
    // catálogo— y no algo que este método pueda improvisar.
    private void ApplyBillingAccount(
        QuotationBillingAccount? billingAccount,
        IReadOnlyDictionary<Guid, QuotationItemPricing>? repricing,
        int? freightTaxPercentageWithVat,
        DateTimeOffset occurredAt)
    {
        var chargedVat = ChargesVat;
        BillingAccount = FrozenVat(billingAccount?.Normalized());
        var currency = CurrencyFor(BillingAccount);
        if (currency == Currency)
        {
            if (chargedVat != ChargesVat)
            {
                ApplyChargesVat(freightTaxPercentageWithVat, occurredAt);
            }

            return;
        }

        // La regla guardada es de la moneda vieja: se descarta y las líneas nacen con la de siempre de
        // la nueva. La del tenant para esa moneda la baja el recálculo (UsePriceRounding) a continuación.
        _priceRoundingDecimals = null;
        _priceRoundingMode = null;
        var rounding = PriceRounding.LegacyFor(currency);
        if (ProjectScope is { } scope)
        {
            // En un proyecto el precio lo escribe el asesor y no hay catálogo que consultar (spec 2026-11-26 §5): lo
            // escrito en la moneda vieja se borra y la línea queda en 0 hasta que se vuelva a escribir.
            AssignProjectScope(scope.WithoutPrices());
            foreach (var item in _items)
            {
                item.ChangeUnitPrice(0m, ChargesVat, currency, rounding, occurredAt);
            }
        }
        else
        {
            foreach (var item in _items)
            {
                if (repricing is null || !repricing.TryGetValue(item.VariantId, out var pricing))
                {
                    throw new QuotationsDomainException(
                        "quotation.quotation.repricing_incomplete",
                        "Every line needs a price in the new currency before the quotation can change currency.");
                }

                item.Reprice(pricing, currency, ChargesVat, rounding, occurredAt);
            }
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
    /// y la foto de tasas de retención y el excedente de IVA pasan a ser los del cliente nuevo (spec
    /// impuestos por tipo §7.6, la única recopia del cliente en una cotización), así que los totales
    /// cambian. Los selectores de facturación propia se vacían: se eligieron para otro cliente.
    ///
    /// Lo que **no** cambia: el número de cotización y sus líneas de producto. El número ya se
    /// emitió una vez y las líneas no dependen del cliente.
    ///
    /// Es el tercer disparador de la re-resolución (spec ReteICA por empresa §7.6): la fuente del cliente vuelve a armarse
    /// con sus tasas nacionales y la regla del cliente en el municipio de la operación, que no cambia. Los porcentajes
    /// editados que se pierden (de las dos fuentes) vuelven en el resultado, para el historial.
    /// </summary>
    public MunicipalReResolution ChangeClient(
        Guid clientId,
        bool customerVatSurplus,
        IReadOnlyList<WithholdingRate> customerWithholdings,
        MunicipalContext municipal,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(customerWithholdings);
        ArgumentNullException.ThrowIfNull(municipal);
        EnsureEditable();
        IReadOnlyList<WithholdingRate> discarded =
        [
            .. _withholdings
                .Where(row => row.Overridden && MunicipalReResolver.IsMunicipal(row.Rate))
                .OrderBy(row => row.Source)
                .ThenBy(row => row.Position)
                .Select(row => row.Rate),
        ];

        ClientId = EnsureValidClientId(clientId);
        Assign(QuotationParties.Empty);
        // El cliente nuevo puede no ser una empresa: la eleccion de nombre vuelve al default.
        BillingUsesBusinessName = false;
        // Facturar a consumidor final se decidió mirando al cliente anterior: el nuevo arranca
        // con la facturación por defecto, y con sus retenciones y su excedente de IVA aplicados.
        BillsToFinalConsumer = false;
        // Las partes se resetean a Empty arriba: sin fila de facturación propia, el excedente vuelve a
        // "sin contestar" — lo que responda el cliente nuevo lo trae CustomerVatSurplus.
        PartyVatSurplus = null;
        CustomerVatSurplus = customerVatSurplus;
        ReplaceWithholdings(
            WithholdingSource.Customer,
            MunicipalReResolver.ByCustomerRule(customerWithholdings, municipal, OperationCityId));
        ReplaceWithholdings(WithholdingSource.Billing, []);
        Touch(updatedBy, occurredAt);
        return new MunicipalReResolution(true, OperationCityId, OperationCityId, discarded, []);
    }

    /// <summary>
    /// Cambia el porcentaje efectivo de la retención municipal de <paramref name="type"/> en la fuente vigente (spec
    /// ReteICA por empresa §7.7, owner R7); <c>null</c> vuelve al del catálogo. El del catálogo no se mueve. Devuelve el
    /// cambio para el historial, o null si el porcentaje ya era ese.
    /// </summary>
    public WithholdingPercentageChange? SetWithholdingPercentage(
        string type,
        decimal? percentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();
        var row = ActiveWithholdings.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.Ordinal));
        var target = MunicipalReResolver.EnsureEditablePercentage(row, percentage, "quotation.billing");
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
    /// Pone en cada tasa con base mínima en unidades la UVT vigente de su país y recalcula (spec retenciones por UVT §5.4,
    /// owner P1): la de un documento editable sigue a la que hoy daría la foto. La clave es el país canónico. No es una
    /// edición del asesor: no sube la versión ni cambia el autor; el handler que guarda o previsualiza ya lo hace.
    /// </summary>
    public void RefreshTaxUnits(IReadOnlyDictionary<string, TaxUnitPhoto> byCountry)
    {
        EnsureEditable();
        DocumentWithholding.RefreshTaxUnits(_withholdings, byCountry);
        RecalculateTotals();
    }

    /// <summary>
    /// «Sin retención» (owner 2026-11-13, plan P12): quita de los selectores de facturación propia la fila de cada tipo
    /// marcado, sea o no la fuente vigente; la del cliente no se toca. Sin fila no hace nada. Que los tipos sean
    /// municipales lo valida la aplicación. Devuelve las filas quitadas que tenían porcentaje editado, para el historial.
    /// </summary>
    public IReadOnlyList<WithholdingRate> ClearMunicipalWithholdings(
        IReadOnlyCollection<string> types,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(types);
        EnsureEditable();
        var current = BillingWithholdings.Select(row => row.Rate).ToList();
        var removed = current.Where(rate => types.Contains(rate.Type, StringComparer.Ordinal)).ToList();
        if (removed.Count == 0)
        {
            return [];
        }

        ReplaceWithholdings(WithholdingSource.Billing, [.. current.Except(removed)]);
        Touch(updatedBy, occurredAt);
        return [.. removed.Where(rate => rate.Overridden)];
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

        EnsureProjectComplete();

        if (ValidUntil is null)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.valid_until_required",
                "A quotation must have a validity date before it can be sent.");
        }

        // Sin empresa facturadora no hay país, y sin país no se sabe qué retenciones aplican (spec impuestos
        // por tipo §7.7): la cotización que le llega al cliente tiene que decir lo que va a cobrar. Guardar un
        // borrador sin ella sigue permitido.
        if (!HasBillingCompany)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing company before it can be sent.");
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

        // Antes que «sin productos»: un proyecto sin servicio no tiene líneas, y ése no es el motivo.
        EnsureProjectComplete();

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

        // Sin empresa emisora el pedido no sabe quién lo emite. La cuenta sí puede faltar (spec 2026-11-17 §4.2): el
        // pedido nace con «Pago: por definir».
        if (!HasBillingCompany)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing company before it can be converted to an order.");
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

        // Con datos propios de facturación nadie más dice si hay excedente de IVA: null es "todavía no se
        // contestó", y un pedido no puede nacer con esa pregunta abierta (con los datos del cliente,
        // CustomerVatSurplus ya la responde). Las retenciones no se preguntan: selectores vacíos son una
        // respuesta válida y no bloquean (spec impuestos por tipo §7.8).
        if (Billing is not null && PartyVatSurplus is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.tax_profile_required",
                "The billing party must state whether it has VAT surplus before converting to an order.");
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
        && !ProjectIsIncomplete
        && _items.Count > 0
        && ValidUntil is not null
        && HasBillingCompany
        && (Billing is not { } billing || billing.IsComplete)
        && (Shipping is not { } shipping || shipping.IsComplete)
        && !BillingLacksIdentification
        && (Billing is null || PartyVatSurplus is not null);

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

    // Las líneas de un proyecto salen de su alcance (spec 2026-11-26 §5).
    private void EnsureManualLines()
    {
        if (Kind == QuotationKind.Project)
        {
            throw new QuotationsDomainException(
                ProjectErrors.ItemsNotAllowed,
                "En una cotización de proyecto no agregas ni cambias productos a mano: la línea sale del alcance.");
        }
    }

    // Completo es con servicio, precio y la línea que lo cobra: sin cualquiera de los tres no hay qué enviar ni vender.
    private bool ProjectIsIncomplete =>
        Kind == QuotationKind.Project && (ProjectScope is not { IsComplete: true } || _items.Count == 0);

    // Enviar y convertir un proyecto exigen servicio y precio (spec 2026-11-26 §6.5).
    private void EnsureProjectComplete()
    {
        if (ProjectIsIncomplete)
        {
            throw new QuotationsDomainException(
                ProjectErrors.Incomplete,
                "Elige el servicio y completa el precio del proyecto antes de enviar o convertir la cotización.");
        }
    }

    private void AssignProjectScope(ProjectScope scope)
    {
        ProjectScope = scope;
        ProjectPricingMode = scope.PricingMode;
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
    /// <summary>La clave externa se pone una vez, al crear el borrador y antes de guardarlo; después no cambia.</summary>
    public void AssignExternalKey(string externalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKey);
        if (ExternalKey is not null)
        {
            throw new InvalidOperationException("The quotation already has an external key.");
        }

        var trimmed = externalKey.Trim();
        ExternalKey = trimmed.Length > ExternalKeyMaxLength
            ? throw new QuotationsDomainException(
                "quotation.quotation.external_key_too_long",
                $"The external key cannot exceed {ExternalKeyMaxLength} characters.")
            : trimmed;
    }

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

    private IEnumerable<DocumentWithholding> WithholdingsOf(WithholdingSource source) =>
        _withholdings.Where(withholding => withholding.Source == source).OrderBy(withholding => withholding.Position);

    private void ReplaceWithholdings(WithholdingSource source, IReadOnlyList<WithholdingRate> rates) =>
        DocumentWithholding.ReplaceSource(_withholdings, source, rates);

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
    // Las retenciones: la regla de candidatas (spec §7.5 pasos 1–4) vive en WithholdingCandidates, compartida
    // con el pedido; cada fila de la foto se liquida otra vez, también las que no aplican.
    private void RecalculateTotals()
    {
        var activeSource = ActiveWithholdingSource;
        var totals = DocumentTotals.Compute(
            LineAmounts,
            FreightAmounts.For(FreightChargeMode, FreightAmount, FreightTaxPercentage, Currency, PriceRounding),
            AppliesVatSurplus,
            WithholdingCandidates.For(
                WithholdingsOf(activeSource).Select(withholding => withholding.Rate),
                BillsToFinalConsumer,
                BillingAccount,
                Currency,
                OperationCityId));
        DocumentWithholding.SettleAll(_withholdings, activeSource, totals.Withholdings);
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
