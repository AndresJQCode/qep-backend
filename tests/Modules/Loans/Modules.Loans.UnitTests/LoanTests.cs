using System.Globalization;
using Modules.Loans.Domain;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class LoanTests
{
    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<LoansDomainException>(act).Code);

    [Fact]
    public void ALoanBecomesSettledOnlyWithoutCapitalAndWithoutPendingInstallments()
    {
        var loan = Loan();
        loan.RefreshStatus(0, Now);
        Assert.Equal(LoanStatus.Active, loan.Status);

        loan.SetOutstanding(0m, Now);
        loan.RefreshStatus(1, Now);
        Assert.Equal(LoanStatus.Active, loan.Status);

        loan.RefreshStatus(0, Now);
        Assert.Equal(LoanStatus.Settled, loan.Status);

        loan.SetOutstanding(10m, Now);
        loan.RefreshStatus(0, Now);
        Assert.Equal(LoanStatus.Active, loan.Status);
    }

    [Fact]
    public void AFutureDisbursementIsRejected() =>
        AssertCode(LoansErrorCodes.LoanDisbursedInFuture, () => Loan(disbursedOn: "2026-11-01", today: "2026-10-31"));

    [Theory]
    [InlineData("EndOfMonth", "2026-10-10", "2026-10-20", "2026-10-31")]
    [InlineData("DisbursementDay", "2026-10-12", "2026-10-20", "2026-11-11")]
    [InlineData("DisbursementDay", "2026-01-31", "2026-02-10", "2026-02-27")]
    [InlineData("EndOfMonth", "2026-10-10", "2026-10-31", "2026-10-31")] // hoy es el corte
    public void TheNextCutoffFollowsTheCutoffRule(string rule, string disbursedOn, string today, string expected)
    {
        var loan = Loan(disbursedOn, Enum.Parse<LoanCutoffRule>(rule), today: today);

        Assert.Equal(Date(expected), loan.NextCutoffDate(Date(today)));
    }

    [Fact]
    public void ALoanWithoutCapitalHasNoNextCutoff()
    {
        var loan = Loan();
        loan.SetOutstanding(0m, Now);
        Assert.Null(loan.NextCutoffDate(Date("2026-10-20")));

        loan.RefreshStatus(0, Now);
        Assert.Equal(LoanStatus.Settled, loan.Status);
        Assert.Null(loan.NextCutoffDate(Date("2026-10-20")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100.0001")]
    public void ARateOutsideTheRangeIsRejected(string rate) =>
        AssertCode(LoansErrorCodes.LoanInvalid, () => Loan(rate: decimal.Parse(rate, CultureInfo.InvariantCulture)));

    [Fact]
    public void TheNextPeriodStartsOnTheDisbursementAndThenFollowsTheLastGenerated()
    {
        var loan = Loan("2026-10-10");
        Assert.Equal(new LoanPeriod(Date("2026-10-01"), Date("2026-10-31")), loan.NextPeriodToGenerate());

        loan.MarkGenerated(Date("2026-10-31"), Now);

        Assert.Equal(new LoanPeriod(Date("2026-11-01"), Date("2026-11-30")), loan.NextPeriodToGenerate());
    }

    [Fact]
    public void MarkGeneratedAndSetOutstandingBumpTheVersion()
    {
        var loan = Loan();
        var version = loan.Version;

        loan.MarkGenerated(Date("2026-10-31"), Now);
        loan.SetOutstanding(50m, Now);

        Assert.Equal(version + 2, loan.Version);
        Assert.Equal(50m, loan.OutstandingPrincipal);
    }

    [Fact]
    public void ChangeTermsForgetsTheGeneratedPeriods()
    {
        // Una marca de período con interés 0,00 sobrevive a HasMovement (no hay cuota ni abono): con otras condiciones el
        // período hay que volver a cobrarlo.
        var loan = Loan();
        loan.MarkGenerated(Date("2026-10-31"), Now);

        loan.ChangeTerms(loan.Terms with { PrincipalAmount = 50m }, Date("2026-10-31"), Now);

        Assert.Null(loan.LastGeneratedPeriodEnd);
        Assert.Equal(new LoanPeriod(Date("2026-10-01"), Date("2026-10-31")), loan.NextPeriodToGenerate());
    }

    [Fact]
    public void ChangeTermsReplacesTheTermsAndResetsTheCapital()
    {
        var loan = Loan();
        var terms = loan.Terms with { PrincipalAmount = 50m, MonthlyRatePercent = 2m };

        loan.ChangeTerms(terms, Date("2026-10-31"), Now);

        Assert.Equal(50m, loan.PrincipalAmount);
        Assert.Equal(50m, loan.OutstandingPrincipal);
        Assert.Equal(2m, loan.MonthlyRatePercent);
    }

    [Fact]
    public void UpdateDetailsTrimsTheNameAndRejectsAnEmptyOne()
    {
        var loan = Loan();
        loan.UpdateDetails("  Otro  ", null, " nota ", Now);

        Assert.Equal("Otro", loan.Name);
        Assert.Equal("nota", loan.Note);
        AssertCode(LoansErrorCodes.LoanInvalid, () => loan.UpdateDetails("  ", null, null, Now));
    }
}
