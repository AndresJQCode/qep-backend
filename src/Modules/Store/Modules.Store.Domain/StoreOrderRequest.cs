namespace Modules.Store.Domain;

/// <summary>
/// Una solicitud de pedido de la tienda pública: <c>New → Converted | Discarded</c>. Congela lo que
/// vio el visitante (líneas, totales, moneda) y lo que aceptó. «Tomar» la convierte en pedido;
/// mientras eso corre, un lease corto evita que dos asesores la tomen a la vez.
/// </summary>
public sealed class StoreOrderRequest
{
    public const int NoteMaxLength = 1000;

    public const int DiscardNoteMaxLength = 500;

    private readonly List<StoreOrderRequestLine> _lines = [];

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private StoreOrderRequest()
    {
        Billing = null!;
        Consent = null!;
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public long Number { get; private set; }

    public Guid ClientRequestId { get; private set; }

    public StoreOrderRequestStatus Status { get; private set; }

    public StoreRequestBilling Billing { get; private set; }

    /// <summary>Null = «Usar los mismos datos para el envío».</summary>
    public StoreRequestShipping? Shipping { get; private set; }

    public string? Note { get; private set; }

    public string Currency { get; private set; }

    public StorePricingMode PricingMode { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal Total { get; private set; }

    public StoreConsent Consent { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public IReadOnlyList<StoreOrderRequestLine> Lines => _lines;

    public Guid? LeaseMemberId { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public Guid? ConvertedBy { get; private set; }

    public DateTimeOffset? ConvertedAt { get; private set; }

    public Guid? OrderId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string? OrderNumber { get; private set; }

    public decimal? OrderTotal { get; private set; }

    /// <summary>Total del pedido al convertir menos el que vio el visitante; null cuando coinciden.</summary>
    public decimal? TotalDifference { get; private set; }

    public Guid? DiscardedBy { get; private set; }

    public DateTimeOffset? DiscardedAt { get; private set; }

    public StoreDiscardReason? DiscardReason { get; private set; }

    public string? DiscardNote { get; private set; }

    public long Version { get; private set; }

    public string DisplayNumber => $"T-{Number:D4}";

    public bool HasScaleDiscount => _lines.Any(l => l.DiscountPercentage > 0m);

    public static StoreOrderRequest Create(
        Guid id,
        Guid tenantId,
        long number,
        Guid clientRequestId,
        StoreRequestBilling billing,
        StoreRequestShipping? shipping,
        string? note,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<StoreOrderRequestLineInput> lines,
        decimal subtotal,
        decimal discountAmount,
        decimal taxAmount,
        decimal total,
        StoreConsent consent,
        DateTimeOffset receivedAt)
    {
        if (lines.Count == 0)
        {
            throw new StoreDomainException(StoreErrorCodes.ValidationFailed, "La solicitud necesita al menos una línea.");
        }

        if (!OnlineStore.IsCurrencyShape(currency))
        {
            throw new StoreDomainException(StoreErrorCodes.ValidationFailed, "La moneda debe ser de 3 letras.");
        }

        var request = new StoreOrderRequest
        {
            Id = id,
            TenantId = tenantId,
            Number = number,
            ClientRequestId = clientRequestId,
            Status = StoreOrderRequestStatus.New,
            Billing = billing ?? throw new ArgumentNullException(nameof(billing)),
            Shipping = shipping,
            Note = StoreText.Optional(note, NoteMaxLength, "La nota"),
            Currency = currency.ToUpperInvariant(),
            PricingMode = mode,
            Subtotal = subtotal,
            DiscountAmount = discountAmount,
            TaxAmount = taxAmount,
            Total = total,
            Consent = consent ?? throw new ArgumentNullException(nameof(consent)),
            ReceivedAt = receivedAt,
            Version = 1,
        };

        for (var i = 0; i < lines.Count; i++)
        {
            request._lines.Add(StoreOrderRequestLine.From(i, lines[i]));
        }

        return request;
    }

    /// <summary>Hay un lease vigente de alguien: la UI deshabilita «Tomar».</summary>
    public bool IsBeingTaken(DateTimeOffset now) => LeaseMemberId is not null && LeaseUntil > now;

    /// <summary>
    /// Reserva la conversión. Quien ya la tiene la renueva (reintenta tras un corte); otro asesor
    /// espera a que venza, que es lo que rescata una conversión que se cayó a medias.
    /// </summary>
    public void BeginConversion(Guid memberId, DateTimeOffset now, TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "El lease debe ser positivo.");
        }

        RequireNew();
        RequireNoLeaseOfAnother(memberId, now);

        LeaseMemberId = memberId;
        LeaseUntil = now + lease;
        Version++;
    }

    public void ReleaseConversion()
    {
        if (LeaseMemberId is null)
        {
            return;
        }

        ClearLease();
        Version++;
    }

    public void MarkConverted(
        Guid orderId,
        Guid customerId,
        string orderNumber,
        decimal orderTotal,
        Guid convertedBy,
        DateTimeOffset now)
    {
        RequireNew();

        var difference = Math.Round(orderTotal - Total, 2, MidpointRounding.AwayFromZero);

        Status = StoreOrderRequestStatus.Converted;
        OrderId = orderId;
        CustomerId = customerId;
        OrderNumber = orderNumber;
        OrderTotal = orderTotal;
        TotalDifference = difference == 0m ? null : difference;
        ConvertedBy = convertedBy;
        ConvertedAt = now;
        ClearLease();
        Version++;
    }

    public void Discard(Guid memberId, StoreDiscardReason reason, string? note, DateTimeOffset now)
    {
        RequireNew();
        RequireNoLeaseOfAnother(memberId, now);

        var cleaned = StoreText.Optional(note, DiscardNoteMaxLength, "La nota");
        if (reason == StoreDiscardReason.Other && cleaned is null)
        {
            throw new StoreDomainException(StoreErrorCodes.ValidationFailed, "Descartar por «otro» exige una nota.");
        }

        Status = StoreOrderRequestStatus.Discarded;
        DiscardedBy = memberId;
        DiscardedAt = now;
        DiscardReason = reason;
        DiscardNote = cleaned;
        ClearLease();
        Version++;
    }

    private void RequireNew()
    {
        if (Status != StoreOrderRequestStatus.New)
        {
            throw new StoreDomainException(StoreErrorCodes.NotNew, "La solicitud ya fue convertida o descartada.");
        }
    }

    private void RequireNoLeaseOfAnother(Guid memberId, DateTimeOffset now)
    {
        if (IsBeingTaken(now) && LeaseMemberId != memberId)
        {
            throw new StoreDomainException(StoreErrorCodes.BeingTaken, "Otro asesor está tomando esta solicitud.");
        }
    }

    private void ClearLease()
    {
        LeaseMemberId = null;
        LeaseUntil = null;
    }
}
