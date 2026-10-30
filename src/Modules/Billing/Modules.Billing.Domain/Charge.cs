namespace Modules.Billing.Domain;

/// <summary>Un cobro de un período de un servicio. El monto y la moneda se copian al generarlo.</summary>
public sealed class Charge
{
    public const int PaymentNoteMaxLength = 1000;

    private Charge()
    {
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public BillingServiceId ServiceId { get; private set; }

    public Guid CustomerId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public DateOnly DueDate { get; private set; }

    public ChargeStatus Status { get; private set; }

    public DateOnly? PaidOn { get; private set; }

    public string? PaymentNote { get; private set; }

    public Guid? PaidByMemberId { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Charge Generate(BillingService service, DateOnly dueDate, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = service.TenantId,
        ServiceId = service.Id,
        CustomerId = service.CustomerId,
        Amount = service.Price,
        Currency = service.Currency,
        DueDate = dueDate,
        Status = ChargeStatus.Pending,
        Version = 1,
        CreatedAt = now,
    };

    public void MarkPaid(DateOnly paidOn, string? note, Guid paidByMemberId, DateOnly today, DateTimeOffset now)
    {
        if (Status != ChargeStatus.Pending)
        {
            throw new BillingDomainException(BillingErrorCodes.ChargeNotPending, "Only a pending charge can be marked as paid.");
        }

        var cleanNote = ValidatePayment(paidOn, note, today);
        Status = ChargeStatus.Paid;
        PaidOn = paidOn;
        PaymentNote = cleanNote;
        PaidByMemberId = paidByMemberId;
        PaidAt = now;
        Version++;
    }

    public void UpdatePayment(DateOnly paidOn, string? note, DateOnly today, DateTimeOffset now)
    {
        if (Status != ChargeStatus.Paid)
        {
            throw new BillingDomainException(BillingErrorCodes.ChargeNotPaid, "Only a paid charge has a payment to edit.");
        }

        var cleanNote = ValidatePayment(paidOn, note, today);
        PaidOn = paidOn;
        PaymentNote = cleanNote;
        Version++;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != ChargeStatus.Pending)
        {
            throw new BillingDomainException(BillingErrorCodes.ChargeNotPending, "Only a pending charge can be cancelled.");
        }

        Status = ChargeStatus.Cancelled;
        Version++;
    }

    public bool IsOverdue(DateOnly today) => Status == ChargeStatus.Pending && DueDate < today;

    private static string? ValidatePayment(DateOnly paidOn, string? note, DateOnly today)
    {
        if (paidOn > today)
        {
            throw new BillingDomainException(BillingErrorCodes.PaymentDateInFuture, "The payment date cannot be in the future.");
        }

        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (clean is { Length: > PaymentNoteMaxLength })
        {
            throw new BillingDomainException(BillingErrorCodes.PaymentNoteTooLong, $"The note cannot exceed {PaymentNoteMaxLength} characters.");
        }

        return clean;
    }
}
