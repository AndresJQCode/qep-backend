using Modules.Loans.Domain;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class LoanInstallmentGeneratorTests
{
    [Fact]
    public void AClosedPeriodBecomesOneInstallmentWithItsSegments()
    {
        var loan = Loan();
        var payments = new[] { Payment(loan, 40_000_000m, "2026-10-15", 60_000_000m) };

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now);

        var installment = Assert.Single(installments);
        Assert.Equal(1170967.74m, installment.Amount);
        Assert.Equal(31, installment.PeriodDays);
        Assert.Equal(2, installment.Segments.Count);
        Assert.Equal(Date("2026-10-01"), installment.PeriodStart);
        Assert.Equal(Date("2026-10-31"), installment.PeriodEnd);
        Assert.Equal(Date("2026-10-31"), loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void TheOpenPeriodIsNotGenerated()
    {
        var loan = Loan();

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date("2026-10-31"), Now);

        Assert.Empty(installments);
        Assert.Null(loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void ACatchUpGeneratesEveryMissedPeriodWithItsRealDates()
    {
        var loan = Loan();

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date("2027-01-15"), Now);

        Assert.Equal(3, installments.Count);
        Assert.All(installments, installment => Assert.Equal(1500000.00m, installment.Amount));
        Assert.Equal(
            [Date("2026-10-01"), Date("2026-11-01"), Date("2026-12-01")],
            installments.Select(installment => installment.PeriodStart));
        Assert.Equal(
            [Date("2026-10-31"), Date("2026-11-30"), Date("2026-12-31")],
            installments.Select(installment => installment.PeriodEnd));
        Assert.Equal([31, 30, 31], installments.Select(installment => installment.PeriodDays));
    }

    [Fact]
    public void AFirstPartialPeriodStartsOnTheDisbursement()
    {
        var loan = Loan("2026-10-10");

        var installment = Assert.Single(LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date("2026-11-01"), Now));

        Assert.Equal(Date("2026-10-10"), installment.PeriodStart);
        Assert.Equal(Date("2026-10-31"), installment.PeriodEnd);
        Assert.Equal(31, installment.PeriodDays);
        Assert.Equal(1064516.13m, installment.Amount);
    }

    [Fact]
    public void AGeneratedInstallmentIgnoresLaterPayments()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var installment = Assert.Single(LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now));

        payments.Add(Payment(loan, 40_000_000m, "2026-11-10", 60_000_000m));
        var next = LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-20"), Now);

        Assert.Equal(1500000.00m, installment.Amount);
        Assert.Empty(next);
    }

    [Fact]
    public void ASettledLoanGeneratesNothing()
    {
        var loan = Loan();
        loan.SetOutstanding(0m, Now);

        Assert.Empty(LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date("2027-01-15"), Now));
    }

    [Fact]
    public void APeriodWhoseInterestRoundsToZeroCreatesNoInstallmentButIsStillMarkedGenerated()
    {
        // 0,01 al 1,5 % mensual causa 0,00015 en el mes: redondea a 0,00 y una cuota en cero no se cobra.
        var loan = Loan(principal: 0.01m);

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date("2026-11-01"), Now);

        Assert.Empty(installments);
        Assert.Equal(Date("2026-10-31"), loan.LastGeneratedPeriodEnd);
    }
}
