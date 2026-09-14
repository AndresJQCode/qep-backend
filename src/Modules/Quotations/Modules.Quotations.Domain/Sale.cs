namespace Modules.Quotations.Domain;

/// <summary>
/// Una venta, creada al convertir una cotización aprobada (US-13 a US-17,
/// modelo-datos-cotizaciones.md §2.4). 1:1 con <see cref="Quotation"/> — no duplica cliente, CUC,
/// productos ni totales; todo eso se referencia por <see cref="QuotationId"/>. Agregado raíz que
/// incluye sus comprobantes de pago (<see cref="SalePaymentProof"/>): nacen con ella, pero
/// mientras sigue <see cref="SaleStatus.Pending"/> se le pueden sumar más, y corregir el monto
/// de los que ya tiene —ver <see cref="AddPaymentProofs"/>— para el caso real de que falte
/// alguno al convertir, el pago se complete después, o alguien haya tipeado mal un monto. No se
/// quitan ni se les cambia el archivo: no hay motivo de negocio para eso.
///
/// Nunca tiene PDF ni envío propio (US-17): el único documento que el cliente recibe es la
/// cotización original.
/// </summary>
public sealed class Sale
{
    public const int SaleNumberMaxLength = 20;
    public const int NotesMaxLength = 500;

    private readonly List<SalePaymentProof> _paymentProofs = [];

    private Sale()
    {
        SaleNumber = string.Empty;
    }

    private Sale(
        SaleId id,
        Guid tenantId,
        string saleNumber,
        QuotationId quotationId,
        SalePaymentStatus paymentStatus,
        string? notes,
        MemberId convertedBy,
        IReadOnlyCollection<SalePaymentProofInput> proofs,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        SaleNumber = NormalizeSaleNumber(saleNumber);
        QuotationId = quotationId;
        Status = SaleStatus.Pending;
        PaymentStatus = paymentStatus;
        Notes = NormalizeNotes(notes);
        ConvertedAt = occurredAt;
        ConvertedBy = convertedBy;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version = 1;
        AddProofs(proofs, convertedBy, occurredAt);
    }

    public SaleId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant. Formato <c>VEN-2026-0001</c>, emitido por
    /// <c>ISaleNumberGenerator</c> — mismo mecanismo que <c>QuotationNumber</c>.</summary>
    public string SaleNumber { get; private set; }

    /// <summary>1:1 con la cotización de origen — único a nivel de base. Toda la información de
    /// cliente/CUC/productos/totales se lee de ahí; esta venta no la repite
    /// (modelo-datos-cotizaciones.md §1.2).</summary>
    public QuotationId QuotationId { get; private set; }

    public SaleStatus Status { get; private set; }

    public SalePaymentStatus PaymentStatus { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset ConvertedAt { get; private set; }

    public MemberId ConvertedBy { get; private set; }

    /// <summary>Cuándo se aprobó, y quién. Null mientras la venta sigue pendiente de revisión.
    /// Se guardan aparte de <see cref="ConvertedAt"/>/<see cref="ConvertedBy"/> justamente porque
    /// no son la misma persona ni el mismo momento — esa es toda la razón de que el estado
    /// exista.</summary>
    public DateTimeOffset? ApprovedAt { get; private set; }

    public MemberId? ApprovedBy { get; private set; }

    /// <summary>Vacío hasta que se sincronice — placeholder para la integración futura con
    /// Ritual Collection (modelo-datos-cotizaciones.md §2.4). Esta fase no la implementa.</summary>
    public string? RitualCollectionSyncId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyCollection<SalePaymentProof> PaymentProofs => _paymentProofs;

    public static Sale Create(
        SaleId id,
        Guid tenantId,
        string saleNumber,
        QuotationId quotationId,
        SalePaymentStatus paymentStatus,
        string? notes,
        MemberId convertedBy,
        IReadOnlyCollection<SalePaymentProofInput> proofs,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, saleNumber, quotationId, paymentStatus, notes, convertedBy, proofs, occurredAt);

    /// <summary>
    /// El visto bueno de quien revisa. Sólo desde <see cref="SaleStatus.Pending"/>: aprobar dos
    /// veces reescribiría quién y cuándo revisó, y no hay nada que volver a revisar.
    /// </summary>
    public void Approve(MemberId approvedBy, DateTimeOffset occurredAt)
    {
        if (Status != SaleStatus.Pending)
        {
            throw new QuotationsDomainException(
                "sale.sale.not_pending",
                "Only a pending sale can be approved.");
        }

        Status = SaleStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Suma comprobantes a una venta ya creada, corrige el monto de los que ya tenía cargados
    /// (a pedido, 2026-09), y actualiza el estado del pago con lo que corresponda a lo cargado.
    /// Existe porque "Aprobar venta" se bloquea mientras el pago no está completo, y hasta ahora
    /// la única forma de cargar comprobantes era al convertir — si faltó alguno, el pago se
    /// terminó de cobrar después, o alguien tipeó mal un monto, no había forma de arreglarlo sin
    /// recrear la venta entera.
    ///
    /// Sólo sobre <see cref="SaleStatus.Pending"/>: aprobada, la venta es el respaldo de un
    /// cobro que alguien ya revisó con lo que había en ese momento — tocar sus comprobantes ahí
    /// adentro cambiaría lo que esa persona dio por bueno.
    ///
    /// <paramref name="notes"/> reemplaza <see cref="Notes"/> entero (a pedido, 2026-09): la
    /// pantalla que suma comprobantes la precarga con lo que ya había, así que lo normal es que
    /// vuelva igual o corregida, nunca perdida. Null o vacío la borra, mismo criterio que al
    /// crear la venta.
    ///
    /// <paramref name="updatedProofs"/> corrige comprobantes que ya existen —el archivo y quién
    /// lo subió no cambian, sólo el monto—, en la misma llamada que suma los nuevos: un solo
    /// viaje de red para las dos cosas, en vez de uno por cada comprobante que se toca.
    /// </summary>
    public void AddPaymentProofs(
        IReadOnlyCollection<SalePaymentProofInput> proofs,
        SalePaymentStatus paymentStatus,
        string? notes,
        MemberId uploadedBy,
        DateTimeOffset occurredAt,
        IReadOnlyCollection<SalePaymentProofAmountUpdate>? updatedProofs = null)
    {
        updatedProofs ??= [];

        if (Status != SaleStatus.Pending)
        {
            throw new QuotationsDomainException(
                "sale.sale.not_pending",
                "Payment proofs can only be added to a pending sale.");
        }

        if (proofs.Count == 0 && updatedProofs.Count == 0)
        {
            throw new QuotationsDomainException(
                "sale.sale.payment_proof_required",
                "At least one payment proof is required.");
        }

        foreach (var update in updatedProofs)
        {
            var proof = _paymentProofs.FirstOrDefault(candidate => candidate.Id == update.ProofId)
                ?? throw new QuotationsDomainException(
                    "sale.payment_proof.not_found",
                    $"Payment proof '{update.ProofId}' was not found on this sale.");

            proof.UpdateAmount(update.Amount);
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(SalePaymentProof.Create(
                SalePaymentProofId.New(), Id, proof.FileId, proof.Amount, uploadedBy, occurredAt));
        }

        PaymentStatus = paymentStatus;
        Notes = NormalizeNotes(notes);
        UpdatedAt = occurredAt;
        Version++;
    }

    // US-14: "se requiere al menos un comprobante, salvo que el estado del pago sea
    // 'Payment pending'". Va antes de construir las líneas para no dejar una venta a medio
    // armar si el chequeo falla.
    private void AddProofs(
        IReadOnlyCollection<SalePaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)
    {
        if (proofs.Count == 0 && PaymentStatus != SalePaymentStatus.PaymentPending)
        {
            throw new QuotationsDomainException(
                "sale.sale.payment_proof_required",
                "At least one payment proof is required unless the payment status is pending.");
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(SalePaymentProof.Create(
                SalePaymentProofId.New(), Id, proof.FileId, proof.Amount, uploadedBy, occurredAt));
        }
    }

    private static string NormalizeSaleNumber(string saleNumber)
    {
        if (string.IsNullOrWhiteSpace(saleNumber))
        {
            throw new QuotationsDomainException(
                "sale.sale.number_required",
                "The sale number is required.");
        }

        var trimmed = saleNumber.Trim();
        return trimmed.Length > SaleNumberMaxLength
            ? throw new QuotationsDomainException(
                "sale.sale.number_too_long",
                $"The sale number cannot exceed {SaleNumberMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var trimmed = notes.Trim();
        return trimmed.Length > NotesMaxLength
            ? throw new QuotationsDomainException(
                "sale.sale.notes_too_long",
                $"The sale notes cannot exceed {NotesMaxLength} characters.")
            : trimmed;
    }
}

/// <summary>Un comprobante de pago tal como lo manda el cliente, sin id: <see cref="Sale"/>
/// asigna un <see cref="SalePaymentProofId"/> nuevo a cada uno — mismo criterio que
/// <c>PriceScaleInput</c> en Catalog.</summary>
public sealed record SalePaymentProofInput(Guid FileId, decimal Amount);

/// <summary>La corrección de un comprobante que ya existe (a pedido, 2026-09): a diferencia de
/// <see cref="SalePaymentProofInput"/>, sí lleva id — es el que dice cuál comprobante corregir,
/// no uno nuevo que agregar.</summary>
public sealed record SalePaymentProofAmountUpdate(SalePaymentProofId ProofId, decimal Amount);
