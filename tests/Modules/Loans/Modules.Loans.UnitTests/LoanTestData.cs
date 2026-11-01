using System.Globalization;
using Modules.Loans.Domain;

namespace Modules.Loans.UnitTests;

internal static class LoanTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Un préstamo de 100.000.000 al 1,5 % desembolsado el 2026-10-01.</summary>
    public static Loan Loan(
        string disbursedOn = "2026-10-01",
        LoanCutoffRule rule = LoanCutoffRule.EndOfMonth,
        decimal principal = 100_000_000m,
        decimal rate = 1.5m,
        string today = "2026-10-31")
    {
        var terms = new LoanTerms(Guid.NewGuid(), principal, "COP", rate, Date(disbursedOn), rule);
        return Modules.Loans.Domain.Loan.Create(LoanId.New(), Guid.NewGuid(), terms, "Novasteel", null, null, Date(today), Now);
    }

    /// <summary>Un abono ya aplicado: deja el capital en <paramref name="balanceAfter"/>.</summary>
    public static LoanCapitalPayment Payment(Loan loan, decimal amount, string paidOn, decimal balanceAfter)
    {
        var payment = LoanCapitalPayment.Create(loan.TenantId, loan.Id, amount, Date(paidOn), null, Now);
        payment.SetBalanceAfter(balanceAfter);
        return payment;
    }
}
