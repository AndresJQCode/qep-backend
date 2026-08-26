namespace Modules.Quotations.Domain;

/// <summary>
/// Una cotización de un tenant (modelo-datos-cotizaciones.md §2.1). Agregado raíz que incluye sus
/// líneas de producto (<see cref="QuotationItem"/>) — toda mutación de líneas pasa por sus
/// métodos, que recalculan los totales del encabezado, mismo criterio que
/// <c>Product</c>/<c>PriceScale</c> en Catalog.
///
/// Cubre el borrador (crear, agregar/editar/quitar líneas, editar encabezado), el envío (US-12)
/// y la anulación (US-11). La conversión a venta y el vencimiento automático (US-13 a US-19)
/// llegan en fases posteriores.
///
/// Editar (líneas o encabezado) exige <see cref="QuotationStatus.Draft"/> o
/// <see cref="QuotationStatus.Sent"/> — US-10: "se puede editar en Draft y Sent... se bloquea
/// una vez Approved". <see cref="Send"/> sólo sale de Draft; <see cref="Void"/> sale de Draft o
/// de Sent.
/// </summary>
public sealed class Quotation
{
    public const int QuotationNumberMaxLength = 20;
    public const int PaymentMethodMaxLength = 50;

    private readonly List<QuotationItem> _items = [];

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
        QuotationOverrides overrides,
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
        Assign(overrides);
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
    /// sería el error que §1.6 previene explícitamente.</summary>
    public decimal Total { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Sobrescrituras de facturación/entrega para esta cotización (US-6). Null = usa el
    /// dato del cliente maestro. Flat en vez de un solo <c>QuotationOverrides</c> expuesto —
    /// mismo criterio que <c>Customer.Phone/Email/Address</c> frente a
    /// <c>CustomerContactInfo</c>: el value object es sólo la forma de entrada de
    /// <see cref="Create"/>/<see cref="UpdateDetails"/>.</summary>
    public string? BillingNameOverride { get; private set; }

    public string? BillingAddressOverride { get; private set; }

    public string? DeliveryAddressOverride { get; private set; }

    public string? DeliveryCityOverride { get; private set; }

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

    public static Quotation Create(
        QuotationId id,
        Guid tenantId,
        string quotationNumber,
        Guid clientId,
        MemberId advisorId,
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationOverrides overrides,
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
            overrides,
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

    /// <summary>Edita el encabezado (US-6, US-10): forma de pago, vigencia, notas y
    /// sobrescrituras de facturación/entrega. La tasa de impuesto no se edita acá — la trae
    /// cada línea desde su producto (RN-013). Reemplaza el recurso entero, mismo criterio que
    /// <c>Product.Update</c>: lo que no viene se limpia.</summary>
    public void UpdateDetails(
        DateOnly? validUntil,
        string? paymentMethod,
        string? notes,
        QuotationOverrides overrides,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        ValidUntil = validUntil;
        PaymentMethod = NormalizePaymentMethod(paymentMethod);
        Notes = NormalizeNotes(notes);
        Assign(overrides);
        Touch(updatedBy, occurredAt);
    }

    /// <summary>US-12: genera el PDF (fuera de este agregado — la aplicación ya lo validó contra
    /// Storage) y marca la cotización como enviada. Sólo desde <see cref="QuotationStatus.Draft"/>:
    /// no tiene sentido volver a "enviar" algo que ya se envió, y el resto de las transiciones
    /// (Approved, Voided, Expired) tampoco vuelven para atrás a Sent.</summary>
    public void Send(Guid pdfFileId, MemberId sentBy, DateTimeOffset occurredAt)
    {
        if (Status != QuotationStatus.Draft)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_draft",
                "Only a draft quotation can be marked as sent.");
        }

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

    /// <summary>US-16: aprueba la cotización al convertirla en venta. Sólo desde
    /// <see cref="QuotationStatus.Sent"/> — no se convierte un borrador ni una cotización ya
    /// aprobada, anulada o vencida. La aplicación llama a este método y crea el
    /// <see cref="Sale"/> en la misma unidad de trabajo (modelo-datos-cotizaciones.md §3:
    /// "conviene hacerlo en una misma transacción").</summary>
    public void Approve(MemberId approvedBy, DateTimeOffset occurredAt)
    {
        if (Status != QuotationStatus.Sent)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_sent",
                "Only a sent quotation can be approved.");
        }

        Status = QuotationStatus.Approved;
        UpdatedBy = approvedBy;
        UpdatedAt = occurredAt;
        Version++;
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

    // US-10: "se puede editar en Draft y Sent... se bloquea una vez Approved". Cubre también
    // Voided (US-11: "quedan de sólo lectura") y Expired, aunque esta fase todavía no produzca
    // ninguna de las dos desde fuera de este agregado salvo Voided.
    private void EnsureEditable()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.not_editable",
                "The quotation cannot be edited in its current status.");
        }
    }

    // Asigna las cuatro siempre, incluidas las null. Se puede **limpiar** una sobrescritura, no
    // sólo setearla: mismo criterio que Product.Apply/Customer.Assign.
    private void Assign(QuotationOverrides overrides)
    {
        var normalized = overrides.Normalized();
        BillingNameOverride = normalized.BillingName;
        BillingAddressOverride = normalized.BillingAddress;
        DeliveryAddressOverride = normalized.DeliveryAddress;
        DeliveryCityOverride = normalized.DeliveryCity;
    }

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
        TaxAmount = Round(_items.Sum(item => item.TaxAmount));
        TaxPercentage = Subtotal > 0 ? Round(TaxAmount / Subtotal * 100m) : 0m;
        Total = Subtotal + TaxAmount;
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
