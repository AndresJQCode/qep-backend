namespace Modules.Loans.Domain;

public readonly record struct LoanId(Guid Value)
{
    public static LoanId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
