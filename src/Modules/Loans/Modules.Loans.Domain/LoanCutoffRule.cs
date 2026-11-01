namespace Modules.Loans.Domain;

/// <summary>Cómo se parte el tiempo en períodos; cada préstamo lo elige al crearse (regla 6 del spec).</summary>
public enum LoanCutoffRule
{
    EndOfMonth,
    DisbursementDay,
}
