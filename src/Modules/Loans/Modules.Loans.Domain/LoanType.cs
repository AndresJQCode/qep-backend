namespace Modules.Loans.Domain;

/// <summary>Hoy hay uno solo; el campo existe para sumar después "interés y capital" sin migrar.</summary>
public enum LoanType
{
    InterestOnly = 1,
}
