namespace Modules.Quotations.Domain;

/// <summary>
/// Una venta, creada al convertir una cotización aprobada (US-13 a US-17,
/// modelo-datos-cotizaciones.md §2.4). 1:1 con <see cref="Quotation"/> — no duplica cliente, CUC,
/// productos ni totales; todo eso se referencia por <see cref="QuotationId"/>. Agregado raíz que
/// incluye sus comprobantes de pago (<see cref="SalePaymentProof"/>), que nacen con ella y no se
/// agregan ni se quitan después.
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
        Status = SaleStatus.Approved;
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
