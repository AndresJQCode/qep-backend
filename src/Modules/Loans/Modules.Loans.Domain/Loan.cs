namespace Modules.Loans.Domain;

/// <summary>
/// Un préstamo de sólo interés. Guarda las condiciones, el capital pendiente y hasta qué corte ya generó cuotas
/// (<see cref="LastGeneratedPeriodEnd"/>); las cuotas y los abonos son filas aparte.
/// </summary>
public sealed class Loan
{
    public const int NameMaxLength = 150;

    public const int NoteMaxLength = 1000;

    private const decimal MaxMonthlyRatePercent = 100m;

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private Loan()
    {
        Name = string.Empty;
        Currency = string.Empty;
    }

    public LoanId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>FK blanda a Customers: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid CustomerId { get; private set; }

    public string Name { get; private set; }

    public LoanType Type { get; private set; }

    public decimal PrincipalAmount { get; private set; }

    public decimal OutstandingPrincipal { get; private set; }

    public string Currency { get; private set; }

    public decimal MonthlyRatePercent { get; private set; }

    public DateOnly DisbursedOn { get; private set; }

    public LoanCutoffRule CutoffRule { get; private set; }

    public Guid? AssigneeMemberId { get; private set; }

    public string? Note { get; private set; }

    public LoanStatus Status { get; private set; }

    /// <summary>El último día del último período ya convertido en cuota; null mientras no haya ninguna.</summary>
    public DateOnly? LastGeneratedPeriodEnd { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public LoanTerms Terms => new(CustomerId, PrincipalAmount, Currency, MonthlyRatePercent, DisbursedOn, CutoffRule);

    public static Loan Create(
        LoanId id,
        Guid tenantId,
        LoanTerms terms,
        string name,
        Guid? assigneeMemberId,
        string? note,
        DateOnly today,
        DateTimeOffset now)
    {
        var loan = new Loan
        {
            Id = id,
            TenantId = tenantId,
            Type = LoanType.InterestOnly,
            Status = LoanStatus.Active,
            Version = 1,
            CreatedAt = now,
        };
        loan.ApplyTerms(terms, today);
        loan.ApplyDetails(name, assigneeMemberId, note);
        loan.UpdatedAt = now;
        return loan;
    }

    /// <summary>
    /// Reemplaza las condiciones y vuelve el capital pendiente al capital prestado. Sólo vale sin movimiento:
    /// que lo verifique quien llama (<c>loans.loan.terms_locked</c>).
    /// </summary>
    public void ChangeTerms(LoanTerms terms, DateOnly today, DateTimeOffset now)
    {
        ApplyTerms(terms, today);
        // Sin movimiento no hay cuota ni abono, pero puede haber una marca de período con interés 0,00: con otras condiciones
        // esos períodos se vuelven a cobrar.
        LastGeneratedPeriodEnd = null;
        Touch(now);
    }

    public void UpdateDetails(string name, Guid? assigneeMemberId, string? note, DateTimeOffset now)
    {
        ApplyDetails(name, assigneeMemberId, note);
        Touch(now);
    }

    /// <summary>
    /// El próximo período a convertir en cuota, o null si el préstamo está saldado o sin capital. Arranca en el
    /// período del desembolso y sigue por el día siguiente al último generado.
    /// </summary>
    public LoanPeriod? NextPeriodToGenerate()
    {
        if (Status == LoanStatus.Settled || OutstandingPrincipal == 0m)
        {
            return null;
        }

        var anchor = LastGeneratedPeriodEnd is { } last ? last.AddDays(1) : DisbursedOn;
        return LoanCalendar.PeriodContaining(CutoffRule, DisbursedOn, anchor);
    }

    /// <summary>
    /// Sube la versión: un abono o una edición que cargaron el préstamo antes de que se generara la cuota pierden
    /// con 412 y reintentan sobre lo nuevo.
    /// </summary>
    public void MarkGenerated(DateOnly periodEnd, DateTimeOffset now)
    {
        LastGeneratedPeriodEnd = periodEnd;
        Touch(now);
    }

    /// <summary>Sube la versión por la misma razón que <see cref="MarkGenerated"/>: dos abonos en paralelo no pueden pisarse.</summary>
    public void SetOutstanding(decimal principal, DateTimeOffset now)
    {
        OutstandingPrincipal = principal;
        Touch(now);
    }

    /// <summary>Saldado si y sólo si no queda capital ni cuotas pendientes. La versión sube sólo si el estado cambia.</summary>
    public void RefreshStatus(int pendingCount, DateTimeOffset now)
    {
        var status = OutstandingPrincipal == 0m && pendingCount == 0 ? LoanStatus.Settled : LoanStatus.Active;
        if (status == Status)
        {
            return;
        }

        Status = status;
        Touch(now);
    }

    public void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    /// <summary>El corte que cierra el período de <paramref name="today"/>, o null si ya no hay interés que cobrar.</summary>
    public DateOnly? NextCutoffDate(DateOnly today)
    {
        if (Status == LoanStatus.Settled || OutstandingPrincipal == 0m)
        {
            return null;
        }

        return LoanCalendar.PeriodContaining(CutoffRule, DisbursedOn, today).End;
    }

    private void ApplyTerms(LoanTerms terms, DateOnly today)
    {
        var currency = terms.Currency?.Trim() ?? string.Empty;
        if (currency.Length == 0)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, "The currency is required.");
        }

        if (terms.PrincipalAmount <= 0)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, "The principal must be greater than zero.");
        }

        if (terms.MonthlyRatePercent <= 0 || terms.MonthlyRatePercent > MaxMonthlyRatePercent)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, "The monthly rate must be greater than 0 and at most 100.");
        }

        if (terms.DisbursedOn > today)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanDisbursedInFuture, "The disbursement date cannot be in the future.");
        }

        CustomerId = terms.CustomerId;
        PrincipalAmount = terms.PrincipalAmount;
        OutstandingPrincipal = terms.PrincipalAmount;
        Currency = currency;
        MonthlyRatePercent = terms.MonthlyRatePercent;
        DisbursedOn = terms.DisbursedOn;
        CutoffRule = terms.CutoffRule;
    }

    private void ApplyDetails(string name, Guid? assigneeMemberId, string? note)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        if (cleanName.Length is 0 or > NameMaxLength)
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, $"The name must have between 1 and {NameMaxLength} characters.");
        }

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > NoteMaxLength })
        {
            throw new LoansDomainException(LoansErrorCodes.LoanInvalid, $"The note cannot exceed {NoteMaxLength} characters.");
        }

        Name = cleanName;
        AssigneeMemberId = assigneeMemberId;
        Note = cleanNote;
    }
}
