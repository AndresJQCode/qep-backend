namespace Modules.Loans.Domain;

/// <summary>Las condiciones que fijan el interés: lo único que no se puede cambiar una vez hay movimiento.</summary>
public sealed record LoanTerms(
    Guid CustomerId,
    decimal PrincipalAmount,
    string Currency,
    decimal MonthlyRatePercent,
    DateOnly DisbursedOn,
    LoanCutoffRule CutoffRule);
