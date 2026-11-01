namespace Modules.Loans.Domain;

/// <summary>
/// Un abono a capital. Anular no borra la fila: queda con <see cref="VoidedAt"/> y su motivo, y deja de
/// contar en el saldo y en el interés.
/// </summary>
public sealed class LoanCapitalPayment
{
    public const int NoteMaxLength = 1000;

    public const int VoidReasonMaxLength = 500;

    private LoanCapitalPayment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public LoanId LoanId { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly PaidOn { get; private set; }

    public string? Note { get; private set; }

    /// <summary>El capital pendiente después de este abono; lo asigna <see cref="LoanCapitalOperations"/> al operar.</summary>
    public decimal BalanceAfter { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public string? VoidReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static LoanCapitalPayment Create(
        Guid tenantId, LoanId loanId, decimal amount, DateOnly paidOn, string? note, DateTimeOffset now)
    {
        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (clean is { Length: > NoteMaxLength })
        {
            throw new LoansDomainException(LoansErrorCodes.PaymentNoteTooLong, $"The note cannot exceed {NoteMaxLength} characters.");
        }

        return new LoanCapitalPayment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            LoanId = loanId,
            Amount = amount,
            PaidOn = paidOn,
            Note = clean,
            CreatedAt = now,
        };
    }

    public void SetBalanceAfter(decimal balance) => BalanceAfter = balance;

    public void MarkVoided(string reason, DateTimeOffset now)
    {
        if (VoidedAt is not null)
        {
            throw new LoansDomainException(LoansErrorCodes.CapitalPaymentAlreadyVoided, "The capital payment is already voided.");
        }

        var clean = reason?.Trim() ?? string.Empty;
        if (clean.Length > VoidReasonMaxLength)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, $"The reason cannot exceed {VoidReasonMaxLength} characters.");
        }

        VoidedAt = now;
        VoidReason = clean.Length == 0 ? null : clean;
    }
}
