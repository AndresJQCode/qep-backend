namespace Modules.Loans.Domain;

/// <summary>
/// La cuota de interés de un período. El monto sale de los tramos al generarla y no se recalcula: un abono
/// posterior no la toca. El pago se maneja como el de un cobro de Contratos de servicio.
/// </summary>
public sealed class LoanInstallment
{
    public const int PaymentNoteMaxLength = 1000;

    private readonly List<LoanInstallmentSegment> _segments = [];

    private LoanInstallment()
    {
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public LoanId LoanId { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Días del período completo (el divisor del interés); null en las cuotas importadas sin detalle.</summary>
    public int? PeriodDays { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public LoanInstallmentStatus Status { get; private set; }

    public bool IsImported { get; private set; }

    public DateOnly? PaidOn { get; private set; }

    public string? PaymentNote { get; private set; }

    public Guid? PaidByMemberId { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<LoanInstallmentSegment> Segments => _segments;

    public static LoanInstallment Generate(
        Loan loan,
        DateOnly periodStart,
        DateOnly periodEnd,
        int periodDays,
        IReadOnlyList<InterestSegment> segments,
        DateTimeOffset now)
    {
        var installment = new LoanInstallment
        {
            Id = Guid.CreateVersion7(),
            TenantId = loan.TenantId,
            LoanId = loan.Id,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PeriodDays = periodDays,
            Amount = LoanInterestCalculator.Total(segments),
            Currency = loan.Currency,
            Status = LoanInstallmentStatus.Pending,
            IsImported = false,
            Version = 1,
            CreatedAt = now,
        };
        for (var index = 0; index < segments.Count; index++)
        {
            installment._segments.Add(LoanInstallmentSegment.From(installment.Id, index + 1, segments[index]));
        }

        return installment;
    }

    public void MarkPaid(DateOnly paidOn, string? note, Guid paidByMemberId, DateOnly today, DateTimeOffset now)
    {
        if (Status != LoanInstallmentStatus.Pending)
        {
            throw new LoansDomainException(LoansErrorCodes.InstallmentNotPending, "Only a pending installment can be marked as paid.");
        }

        var cleanNote = ValidatePayment(paidOn, note, today);
        Status = LoanInstallmentStatus.Paid;
        PaidOn = paidOn;
        PaymentNote = cleanNote;
        PaidByMemberId = paidByMemberId;
        PaidAt = now;
        Version++;
    }

    public void UpdatePayment(DateOnly paidOn, string? note, DateOnly today, DateTimeOffset now)
    {
        if (Status != LoanInstallmentStatus.Paid)
        {
            throw new LoansDomainException(LoansErrorCodes.InstallmentNotPaid, "Only a paid installment has a payment to edit.");
        }

        var cleanNote = ValidatePayment(paidOn, note, today);
        PaidOn = paidOn;
        PaymentNote = cleanNote;
        Version++;
    }

    public void RemovePayment()
    {
        if (Status != LoanInstallmentStatus.Paid)
        {
            throw new LoansDomainException(LoansErrorCodes.InstallmentNotPaid, "Only a paid installment has a payment to remove.");
        }

        Status = LoanInstallmentStatus.Pending;
        PaidOn = null;
        PaymentNote = null;
        PaidByMemberId = null;
        PaidAt = null;
        Version++;
    }

    private static string? ValidatePayment(DateOnly paidOn, string? note, DateOnly today)
    {
        if (paidOn > today)
        {
            throw new LoansDomainException(LoansErrorCodes.PaymentDateInFuture, "The payment date cannot be in the future.");
        }

        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (clean is { Length: > PaymentNoteMaxLength })
        {
            throw new LoansDomainException(LoansErrorCodes.PaymentNoteTooLong, $"The note cannot exceed {PaymentNoteMaxLength} characters.");
        }

        return clean;
    }
}
