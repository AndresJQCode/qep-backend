namespace Modules.Loans.Domain;

/// <summary>Un período de interés con ambos extremos inclusive.</summary>
public sealed record LoanPeriod(DateOnly Start, DateOnly End)
{
    public int Days { get; } = End.DayNumber - Start.DayNumber + 1;
}
