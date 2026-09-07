namespace Modules.Quotations.Domain;

/// <summary>
/// Una cotización de un tenant (modelo-datos-cotizaciones.md §2.1). Agregado raíz que incluye sus
/// líneas de producto (<see cref="QuotationItem"/>) — toda mutación de líneas pasa por sus
/// métodos, que recalculan los totales del encabezado, mismo criterio que
/// <c>Product</c>/<c>PriceScale</c> en Catalog.
///
/// Cubre el borrador (crear, agregar/editar/quitar líneas, editar encabezado), el envío (US-12),
/// la anulación (US-11), la conversión a venta (US-16, vía <see cref="EnsureConvertibleToSale"/>
/// — no cambia el estado) y el vencimiento automático (US-19).
///
/// Editar (líneas o encabezado) exige <see cref="QuotationStatus.Draft"/> o
/// <see cref="QuotationStatus.Sent"/> — US-10: "se puede editar en Draft y Sent... se bloquea
/// una vez convertida a venta, anulada o vencida". <see cref="Send"/> sólo sale de Draft; <see cref="Void"/> sale de Draft o
/// de Sent.
/// </summary>
public sealed class Quotation
{
    public const int QuotationNumberMaxLength = 20;
    public const int PaymentMethodMaxLength = 50;

    private readonly List<QuotationItem> _items = [];

    private readonly List<QuotationParty> _parties = [];

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
        Assign(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        BillingAccount = billingAccount?.Normalized();
        Currency = BillingAccount is null
            ? QuotationCurrencies.Default
            : QuotationCurrencies.FromCode(BillingAccount.Currency);
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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateOnly? ValidUntil { get; private set; }

    public string? PaymentMethod { get; private set; }

    /// <summary>Suma de los subtotales de línea, ya netos del descuento de cada una.</summary>
    public decimal Subtotal { get; private set; }

    /// <summary>Tasa efectiva de la cotización: <c>TaxAmount / Subtotal × 100</c>, derivada de
    /// la suma del impuesto de cada línea (<see cref="RecalculateTotals"/>) — no es un valor que
    /// se pueda fijar desde fuera, cada producto trae su propia tasa
    /// (<c>Catalog.TaxRate.Percentage</c>). 0 sin líneas o sin subtotal.</summary>
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
    /// <see cref="CustomerWithRetention"/>, si no 0. Informativo hacia <see cref="Total"/> (que
    /// sigue siendo lo facturado) pero resta hacia <see cref="NetTotal"/> — es lo que el cliente
    /// le retiene al vendedor, no algo que la cotización deje de facturar.</summary>
    public decimal RetentionAmount { get; private set; }

    /// <summary>Total - RetentionAmount: lo que efectivamente se cobra en efectivo. Igual a
    /// <see cref="Total"/> cuando no hay retención.</summary>
    public decimal NetTotal { get; private set; }

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
    /// La moneda de **toda** la cotización: precios unitarios, descuentos, impuestos y totales.
    ///
    /// La fija la cuenta de cobro (<see cref="BillingAccount"/>): si se factura a una cuenta en
    /// dólares, la cotización entera se expresa en dólares, con el precio en dólares de cada
    /// producto — no con una conversión, que este módulo no sabe hacer. Sin cuenta elegida es
    /// <see cref="QuotationCurrencies.Default"/>.
    ///
    /// Quitar la cuenta **no** devuelve la cotización a pesos: los precios que ya se cotizaron
    /// quedan como están, y revalorizarlos por un borrado sería un cambio de totales que nadie
    /// pidió. Vuelve a cambiar recién cuando se elige otra cuenta.
    /// </summary>
    public QuotationCurrency Currency { get; private set; }

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
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        // Un producto por cotizacion: dos lineas del mismo producto son la misma linea con la
        // cantidad partida, y partida ademas rompe el descuento por escala (cada mitad resuelve
        // su escala por separado y las dos pagan mas caro que la suma junta). Quien quiera mas
        // unidades cambia la cantidad de la linea que ya existe.
        if (_items.Any(existing => existing.ProductId == productId))
        {
            throw new QuotationsDomainException(
                "quotation.item.duplicate_product",
                "The product is already in the quotation.");
        }

        var item = QuotationItem.Create(
            itemId, Id, productId, quantity, unitPrice, discountPercentage, taxPercentage,
            _items.Count + 1, occurredAt);
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

        FindItem(itemId).UpdateQuantity(quantity, discountPercentage, taxPercentage, occurredAt);
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
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        ValidUntil = validUntil;
        PaymentMethod = NormalizePaymentMethod(paymentMethod);
        Notes = NormalizeNotes(notes);
        Assign(parties);
        BillingUsesBusinessName = parties.BillingUsesBusinessName;
        ApplyBillingAccount(billingAccount, repricing, occurredAt);
        Touch(updatedBy, occurredAt);
    }

    /// <summary>
    /// Cuál sería la moneda de esta cotización con esa cuenta de cobro. La aplicación la consulta
    /// **antes** de guardar, para saber si tiene que ir a buscar los precios de las líneas en otra
    /// moneda; el agregado la vuelve a calcular al aplicar, así que no depende de que el llamador
    /// haya preguntado.
    /// </summary>
    public QuotationCurrency CurrencyFor(QuotationBillingAccount? billingAccount) =>
        billingAccount is null
            ? Currency
            : QuotationCurrencies.FromCode(billingAccount.Currency);

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

        foreach (var item in _items)
        {
            if (repricing is null || !repricing.TryGetValue(item.ProductId, out var pricing))
            {
                throw new QuotationsDomainException(
                    "quotation.quotation.repricing_incomplete",
                    "Every line needs a price in the new currency before the quotation can change currency.");
            }

            item.Reprice(pricing, occurredAt);
        }

        Currency = currency;
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
        CustomerWithRetention = customerWithRetention;
        CustomerVatSurplus = customerVatSurplus;
        Touch(updatedBy, occurredAt);
    }

    /// <summary>US-12: genera el PDF (fuera de este agregado — la aplicación ya lo validó contra
    /// Storage) y marca la cotización como enviada. Desde <see cref="QuotationStatus.Draft"/> o
    /// desde <see cref="QuotationStatus.Sent"/>: reenviar es el mismo hecho para el agregado —
    /// PDF nuevo, <see cref="SentAt"/> nuevo, estado <see cref="QuotationStatus.Sent"/>—, y lo
    /// que lo distingue de un primer envío es la entrada de historial que escribe el caso de
    /// uso, no una transición distinta. Voided y Expired sí siguen sin volver para atrás a Sent.
    ///
    /// <see cref="SentAt"/> y <see cref="UpdatedAt"/> quedan en el mismo instante también en un
    /// reenvío, así que <see cref="HasChangesSinceSent"/> vuelve a <c>false</c>: lo que el
    /// cliente tiene en la mano es, otra vez, la versión vigente.</summary>
    public void Send(Guid pdfFileId, MemberId sentBy, DateTimeOffset occurredAt)
    {
        EnsureSendable();

        PdfFileId = pdfFileId;
        SentAt = occurredAt;
        Status = QuotationStatus.Sent;
        UpdatedBy = sentBy;
        UpdatedAt = occurredAt;
        Version++;
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
    /// <see cref="EnsureConvertibleToSale"/>. Existe aparte para que el caso de uso pueda
    /// comprobarlas **antes** de firmar la URL del PDF y de entregarle el mensaje a WhatsApp:
    /// esos dos son efectos externos que no se deshacen, y una cotización que no puede pasar a
    /// Sent no puede haberle llegado al cliente.
    ///
    /// Sin vigencia la cotización nunca vence: <c>QuotationExpirationProcessor</c> filtra por
    /// <see cref="ValidUntil"/> no nulo, así que una Sent sin fecha quedaría convertible a venta
    /// para siempre, con los precios congelados el día que se envió. Se exige al salir de Draft
    /// porque es el único punto por el que pasa toda cotización antes de
    /// <see cref="EnsureConvertibleToSale"/>.
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
    }

    /// <summary>US-16: valida que se pueda convertir en venta. Sólo desde
    /// <see cref="QuotationStatus.Sent"/> — no se convierte un borrador, ni una ya anulada o
    /// vencida. No muta nada: a diferencia de la vieja <c>Approve()</c>, convertir a venta ya no
    /// cambia el estado de la cotización (no existe un estado "aprobada"/"convertida" — ver
    /// <see cref="QuotationStatus"/>), así que esto es sólo el guard de precondición que
    /// <c>ConvertQuotationToSaleHandler</c> llama antes de crear el <see cref="Sale"/>.</summary>
    public void EnsureConvertibleToSale()
    {
        if (Status != QuotationStatus.Sent)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_sent",
                "Only a sent quotation can be converted to a sale.");
        }

        // Lo que una venta necesita para existir y que la cotizacion puede no tener todavia. Se
        // comprueba aca y no en la pantalla porque es la condicion del negocio, no del formulario:
        // la venta se crea desde este agregado y estos cuatro datos son los que hereda.
        if (_items.Count == 0)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.items_required",
                "A quotation without products cannot be converted to a sale.");
        }

        if (ValidUntil is null)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.valid_until_required",
                "A quotation must have a validity date before it can be converted to a sale.");
        }

        if (string.IsNullOrWhiteSpace(PaymentMethod))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.payment_method_required",
                "A quotation must have a payment method before it can be converted to a sale.");
        }

        // Sin cuenta de cobro la venta no sabe a donde se paga, que es justo lo que una venta
        // tiene que decir.
        if (BillingAccount is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_required",
                "A quotation must have a billing account before it can be converted to a sale.");
        }
    }

    /// <summary>
    /// Si convertir en venta es posible ahora. Misma regla que
    /// <see cref="EnsureConvertibleToSale"/>, en forma de pregunta: la pantalla la usa para
    /// habilitar el boton en vez de reimplementar las condiciones.
    /// </summary>
    public bool CanBeConvertedToSale =>
        Status == QuotationStatus.Sent
        && _items.Count > 0
        && ValidUntil is not null
        && !string.IsNullOrWhiteSpace(PaymentMethod)
        && BillingAccount is not null;

    /// <summary>
    /// Vuelve a tomar del cliente maestro <see cref="CustomerWithRetention"/> y
    /// <see cref="CustomerVatSurplus"/> mientras la cotización sigue editable (Draft o Sent).
    /// Silencioso a propósito (no valida ni lanza) para poder llamarse también desde una
    /// lectura: una vez Voided o Expired la cotización queda tal cual quedó, sin excepción, y
    /// nada la vuelve a tocar.
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

    // US-10: "se puede editar en Draft y Sent... se bloquea una vez convertida a venta". Cubre
    // también Voided (US-11: "quedan de sólo lectura") y Expired — convertir a venta (US-16) no
    // suma un estado propio, así que lo único que EnsureEditable necesita bloquear además de
    // Sent/Draft es Voided y Expired.
    private void EnsureEditable()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_editable",
                "The quotation cannot be edited in its current status.");
        }
    }

    // Reemplaza las dos partes siempre, incluidas las ausentes: `UpdateDetails` reemplaza el
    // recurso entero, así que una parte que llega null borra la fila que hubiera -- que es
    // exactamente "volvé a usar los datos del cliente" (el switch prendido de nuevo).
    private void Assign(QuotationParties parties)
    {
        Assign(QuotationPartyRole.Billing, parties.Billing);
        Assign(QuotationPartyRole.Shipping, parties.Shipping);
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

    private void RecalculateTotals()
    {
        Subtotal = Round(_items.Sum(item => item.Subtotal));
        DiscountAmount = Round(_items.Sum(item => item.DiscountAmount));

        // Un cliente con excedente de IVA no paga IVA en la cotización, cualquiera sea la tasa
        // de cada línea — el impuesto de cada QuotationItem queda intacto (sigue reflejando la
        // tasa real del producto), pero el encabezado lo ignora entero.
        var rawTaxAmount = Round(_items.Sum(item => item.TaxAmount));
        TaxAmount = CustomerVatSurplus ? 0m : rawTaxAmount;
        TaxPercentage = Subtotal > 0 ? Round(TaxAmount / Subtotal * 100m) : 0m;

        Total = Subtotal + TaxAmount;

        // Retención en la fuente: 2.5% de lo facturado sin IVA. Resta del neto a cobrar, no de
        // Total — Total sigue siendo lo facturado, RetentionAmount es lo que el cliente le
        // retiene al vendedor y no paga en efectivo.
        RetentionAmount = CustomerWithRetention ? Round(Subtotal * 0.025m) : 0m;
        NetTotal = Total - RetentionAmount;
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

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
