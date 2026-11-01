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

    // Con DisbursementDay el interés de un período completo es siempre 100.000.000 x 1,5 % = 1.500.000,00, sin importar los días:
    // lo que cambia con el ancla son las fechas reales y los días de cada período.
    [Theory]
    // Ancla 12: 12/oct-11/nov (31 días), 12/nov-11/dic (30 días); el de 12/dic-11/ene sigue abierto el 12/dic.
    [InlineData("2026-10-12", "2026-12-12", "2026-10-12..2026-11-11:31|2026-11-12..2026-12-11:30")]
    // Ancla 31 desde enero: febrero no tiene 31, así que el período cierra el 27/feb y el siguiente arranca el 28/feb (31 días,
    // hasta el 30/mar) y el siguiente vuelve al 31/mar (30 días, hasta el 29/abr): el ancla no se arrastra.
    [InlineData("2026-01-31", "2026-04-30", "2026-01-31..2026-02-27:28|2026-02-28..2026-03-30:31|2026-03-31..2026-04-29:30")]
    public void ADisbursementDayCatchUpUsesTheRealDatesOfTheAnchor(string disbursedOn, string today, string expected)
    {
        var loan = Loan(disbursedOn, LoanCutoffRule.DisbursementDay, today: today);

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Date(today), Now);

        Assert.Equal(
            expected,
            string.Join('|', installments.Select(item => $"{item.PeriodStart:yyyy-MM-dd}..{item.PeriodEnd:yyyy-MM-dd}:{item.PeriodDays}")));
        Assert.All(installments, installment => Assert.Equal(1500000.00m, installment.Amount));
        Assert.Equal(installments[^1].PeriodEnd, loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void ADisbursementDayPeriodWithAPaymentInsideSplitsOnTheRealDays()
    {
        // Ancla 31, período 28/feb-30/mar = 31 días. Abono de 40.000.000 el 15/mar deja 60.000.000 desde ese día.
        //   28/feb-14/mar: 1 + 14 = 15 días sobre 100.000.000: 1.500.000 x 15 / 31 = 725.806,4516 -> 725.806,45
        //   15/mar-30/mar: 16 días sobre 60.000.000: 900.000 x 16 / 31 = 464.516,1290 -> 464.516,13
        //   total 1.190.322,58
        var loan = Loan("2026-01-31", LoanCutoffRule.DisbursementDay, today: "2026-03-31");
        var payments = new[] { Payment(loan, 40_000_000m, "2026-03-15", 60_000_000m) };

        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-03-31"), Now);

        var second = installments[1];
        Assert.Equal((Date("2026-02-28"), Date("2026-03-30")), (second.PeriodStart, second.PeriodEnd));
        Assert.Equal([725806.45m, 464516.13m], second.Segments.Select(segment => segment.Interest));
        Assert.Equal(1190322.58m, second.Amount);
    }
}
