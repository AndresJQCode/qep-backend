using Modules.Loans.Domain;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class LoanAccrualTests
{
    [Fact]
    public void TheAccrualCoversThePeriodUpToToday()
    {
        var loan = Loan();
        var payments = new[] { Payment(loan, 40_000_000m, "2026-10-15", 60_000_000m) };

        var accrual = LoanAccrual.ToDate(loan, payments, Date("2026-10-20"));

        Assert.NotNull(accrual);
        Assert.Equal(851612.90m, accrual.Amount);
        Assert.Equal(new LoanPeriod(Date("2026-10-01"), Date("2026-10-31")), accrual.Period);
        Assert.Equal(2, accrual.Segments.Count);
        Assert.Equal(Date("2026-10-20"), accrual.Segments[^1].End);
    }

    [Fact]
    public void TheFirstPartialPeriodAccruesFromTheDisbursement()
    {
        var loan = Loan("2026-10-10");

        var accrual = LoanAccrual.ToDate(loan, [], Date("2026-10-20"));

        Assert.NotNull(accrual);
        Assert.Equal(Date("2026-10-10"), accrual.Segments[0].Start);
        Assert.Equal(11, accrual.Segments[0].Days);
    }

    [Fact]
    public void ThereIsNoAccrualWithoutCapital()
    {
        var loan = Loan();
        loan.SetOutstanding(0m, Now);

        Assert.Null(LoanAccrual.ToDate(loan, [], Date("2026-10-20")));

        loan.RefreshStatus(0, Now);
        Assert.Null(LoanAccrual.ToDate(loan, [], Date("2026-10-20")));
    }
}
