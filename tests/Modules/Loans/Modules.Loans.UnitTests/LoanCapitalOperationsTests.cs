using Modules.Loans.Domain;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class LoanCapitalOperationsTests
{
    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<LoansDomainException>(act).Code);

    // Aplica el abono como lo hace un handler: opera, y recién ahí lo suma a la lista.
    private static CapitalPaymentOutcome Add(
        Loan loan, List<LoanCapitalPayment> payments, decimal amount, string paidOn, string today = "2026-10-31", int pending = 0)
    {
        var outcome = LoanCapitalOperations.Add(loan, payments, amount, Date(paidOn), null, pending, Date(today), Now);
        payments.Add(outcome.Payment);
        return outcome;
    }

    [Fact]
    public void ACapitalPaymentLowersTheBalanceAndRecordsIt()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();

        var outcome = Add(loan, payments, 40_000_000m, "2026-10-15");

        Assert.Equal(60_000_000m, loan.OutstandingPrincipal);
        Assert.Equal(60_000_000m, outcome.Payment.BalanceAfter);
        Assert.Equal(40_000_000m, outcome.Payment.Amount);
        Assert.Equal(Date("2026-10-15"), outcome.Payment.PaidOn);
        Assert.Null(outcome.FinalInstallment);
        Assert.Equal(LoanStatus.Active, loan.Status);
    }

    [Fact]
    public void PendingInstallmentsBlockThePayment() =>
        AssertCode(
            LoansErrorCodes.CapitalPaymentPendingInstallments,
            () => Add(Loan(), [], 1m, "2026-10-15", pending: 1));

    [Fact]
    public void AnAmountAboveTheBalanceIsRejected() =>
        AssertCode(
            LoansErrorCodes.CapitalPaymentExceedsBalance,
            () => Add(Loan(), [], 100_000_001m, "2026-10-15"));

    [Fact]
    public void ACapitalPaymentOnALoanWithoutCapitalIsRejected()
    {
        var loan = Loan();
        loan.SetOutstanding(0m, Now);

        AssertCode(LoansErrorCodes.CapitalPaymentExceedsBalance, () => Add(loan, [], 1m, "2026-10-15"));
    }

    [Theory]
    [InlineData("2026-09-30")]
    [InlineData("2026-10-21")]
    public void ADateOutsideTheOpenPeriodIsRejected(string paidOn) =>
        AssertCode(
            LoansErrorCodes.CapitalPaymentDateOutOfPeriod,
            () => Add(Loan(), [], 1m, paidOn, today: "2026-10-20"));

    [Fact]
    public void ADateBeforeTheOpenPeriodIsRejected()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now);

        AssertCode(LoansErrorCodes.CapitalPaymentDateOutOfPeriod, () => Add(loan, payments, 1m, "2026-10-31", today: "2026-11-05"));
        Assert.Equal(Date("2026-11-01"), LoanCapitalOperations.OpenFrom(loan));
    }

    [Fact]
    public void SettlingGeneratesTheLastInstallmentUpToTheDayBefore()
    {
        var loan = Loan();

        var outcome = Add(loan, [], 100_000_000m, "2026-10-15");

        var final = outcome.FinalInstallment;
        Assert.NotNull(final);
        Assert.Equal(677419.35m, final.Amount);
        Assert.Equal(Date("2026-10-01"), final.PeriodStart);
        Assert.Equal(Date("2026-10-15"), final.PeriodEnd);
        Assert.Equal(31, final.PeriodDays);
        var segment = Assert.Single(final.Segments);
        Assert.Equal(Date("2026-10-14"), segment.EndOn);
        Assert.Equal(0m, loan.OutstandingPrincipal);
        Assert.Equal(Date("2026-10-15"), loan.LastGeneratedPeriodEnd);
        Assert.Equal(LoanStatus.Active, loan.Status);
    }

    [Fact]
    public void SettlingAfterAnEarlierPaymentAddsBothSegments()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        Add(loan, payments, 40_000_000m, "2026-10-10");

        var outcome = Add(loan, payments, 60_000_000m, "2026-10-20");

        var final = outcome.FinalInstallment;
        Assert.NotNull(final);
        Assert.Equal(2, final.Segments.Count);
        Assert.Equal(435483.87m + 290322.58m, final.Amount);
        Assert.Equal(725806.45m, final.Amount);
    }

    [Fact]
    public void SettlingOnTheFirstDayOfTheOpenPeriodCreatesNoInstallment()
    {
        var loan = Loan();

        var outcome = Add(loan, [], 100_000_000m, "2026-10-01");

        Assert.Null(outcome.FinalInstallment);
        Assert.Equal(LoanStatus.Settled, loan.Status);
        Assert.Equal(0m, loan.OutstandingPrincipal);
        Assert.Null(loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void SettlingWithInterestThatRoundsToZeroCreatesNoInstallment()
    {
        // 10 × 1,5 % × 1 día / 31 = 0,0048: redondea a 0,00 y una cuota en cero no se cobra.
        var loan = Loan(principal: 10m);

        var outcome = Add(loan, [], 10m, "2026-10-02");

        Assert.Null(outcome.FinalInstallment);
        Assert.Equal(LoanStatus.Settled, loan.Status);
        Assert.Equal(0m, loan.OutstandingPrincipal);
        Assert.Null(loan.LastGeneratedPeriodEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnAmountThatIsNotPositiveIsRejected(int amount) =>
        AssertCode(LoansErrorCodes.LoanInvalid, () => Add(Loan(), [], amount, "2026-10-15"));

    [Fact]
    public void ABackdatedPaymentRebalancesTheLaterOnes()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var later = Add(loan, payments, 40_000_000m, "2026-10-20").Payment;

        var earlier = Add(loan, payments, 30_000_000m, "2026-10-10").Payment;

        Assert.Equal(70_000_000m, earlier.BalanceAfter);
        Assert.Equal(30_000_000m, later.BalanceAfter);
        Assert.Equal(30_000_000m, loan.OutstandingPrincipal);

        var installment = Assert.Single(LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now));
        Assert.Equal(435483.87m + 338709.68m + 174193.55m, installment.Amount);
        Assert.Equal(948387.10m, installment.Amount);
    }

    [Fact]
    public void ABackdatedPaymentDoesNotRewriteWhatIsInAClosedPeriod()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment> { Payment(loan, 40_000_000m, "2026-10-15", 25_000_000m) };
        loan.SetOutstanding(25_000_000m, Now);
        LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now);

        Add(loan, payments, 5_000_000m, "2026-11-03", today: "2026-11-05");

        // El BalanceAfter de octubre puede venir de la carga inicial: no se recalcula.
        Assert.Equal(25_000_000m, payments[0].BalanceAfter);
        Assert.Equal(20_000_000m, payments[1].BalanceAfter);
    }

    [Fact]
    public void ABackdatedPaymentThatSettlesClosesOnTheLatestDate()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        Add(loan, payments, 40_000_000m, "2026-10-20");

        var outcome = Add(loan, payments, 60_000_000m, "2026-10-10");

        var final = outcome.FinalInstallment;
        Assert.NotNull(final);
        Assert.Equal(Date("2026-10-20"), final.PeriodEnd);
        Assert.Equal(629032.26m, final.Amount);
        Assert.Equal(2, final.Segments.Count);
        Assert.Equal((100_000_000m, 9), (final.Segments[0].Principal, final.Segments[0].Days));
        Assert.Equal((40_000_000m, 10), (final.Segments[1].Principal, final.Segments[1].Days));
        Assert.Equal(Date("2026-10-20"), loan.LastGeneratedPeriodEnd);
    }

    [Fact]
    public void OnlyTheLatestPaymentCanBeVoided()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        // Se cargó primero el de la fecha más nueva: el último es por fecha, no por orden de carga.
        var newest = Add(loan, payments, 40_000_000m, "2026-10-20").Payment;
        var oldest = Add(loan, payments, 30_000_000m, "2026-10-10").Payment;

        AssertCode(LoansErrorCodes.CapitalPaymentNotLatest, () => LoanCapitalOperations.Void(loan, payments, oldest.Id, "x", 0, Now));
        Assert.Same(newest, LoanCapitalOperations.Voidable(loan, payments));

        LoanCapitalOperations.Void(loan, payments, newest.Id, "x", 0, Now);
        Assert.NotNull(newest.VoidedAt);
    }

    [Fact]
    public void AVoidedPaymentRestoresTheBalanceAndKeepsItsRow()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var first = Add(loan, payments, 40_000_000m, "2026-10-10").Payment;
        var second = Add(loan, payments, 10_000_000m, "2026-10-20").Payment;

        LoanCapitalOperations.Void(loan, payments, second.Id, "  error de digitación ", 0, Now);

        Assert.Equal(60_000_000m, loan.OutstandingPrincipal);
        Assert.Equal(Now, second.VoidedAt);
        Assert.Equal("error de digitación", second.VoidReason);
        Assert.Equal(2, payments.Count);
        Assert.Equal(60_000_000m, first.BalanceAfter);
        Assert.Same(first, LoanCapitalOperations.Voidable(loan, payments));
    }

    [Fact]
    public void VoidingTheSettlingPaymentOnTheFirstDayReopensTheLoan()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var settling = Add(loan, payments, 100_000_000m, "2026-10-01").Payment;
        Assert.Equal(LoanStatus.Settled, loan.Status);

        LoanCapitalOperations.Void(loan, payments, settling.Id, "x", 0, Now);

        Assert.Equal(LoanStatus.Active, loan.Status);
        Assert.Equal(100_000_000m, loan.OutstandingPrincipal);
    }

    [Fact]
    public void APaymentBeforeAGeneratedInstallmentCannotBeVoided()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var payment = Add(loan, payments, 40_000_000m, "2026-10-15").Payment;
        LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now);

        AssertCode(LoansErrorCodes.CapitalPaymentInstallmentGenerated, () => LoanCapitalOperations.Void(loan, payments, payment.Id, "x", 0, Now));
        Assert.Null(LoanCapitalOperations.Voidable(loan, payments));
    }

    [Fact]
    public void APaymentBeforeAZeroInterestMarkIsStillBehindAClosedPeriod()
    {
        // Con 0,01 de capital el interés del mes redondea a 0,00: el período se marca generado sin cuota. El período cerró igual,
        // y anular el abono cambiaría un interés ya fijado sin que nadie lo recalcule, así que sigue sin poder anularse.
        var loan = Loan(principal: 0.02m);
        var payments = new List<LoanCapitalPayment>();
        var payment = Add(loan, payments, 0.01m, "2026-10-15").Payment;
        var generated = LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-01"), Now);

        Assert.Empty(generated);
        Assert.Equal(Date("2026-10-31"), loan.LastGeneratedPeriodEnd);
        AssertCode(LoansErrorCodes.CapitalPaymentInstallmentGenerated, () => LoanCapitalOperations.Void(loan, payments, payment.Id, "x", 0, Now));
    }

    [Fact]
    public void ThePaymentThatGeneratedTheLastInstallmentCannotBeVoided()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var settling = Add(loan, payments, 100_000_000m, "2026-10-15");
        Assert.NotNull(settling.FinalInstallment);

        AssertCode(
            LoansErrorCodes.CapitalPaymentInstallmentGenerated,
            () => LoanCapitalOperations.Void(loan, payments, settling.Payment.Id, "x", 1, Now));
    }

    [Fact]
    public void VoidingTwiceIsRejected()
    {
        var loan = Loan();
        var payments = new List<LoanCapitalPayment>();
        var payment = Add(loan, payments, 40_000_000m, "2026-10-15").Payment;
        LoanCapitalOperations.Void(loan, payments, payment.Id, "x", 0, Now);

        AssertCode(LoansErrorCodes.CapitalPaymentAlreadyVoided, () => LoanCapitalOperations.Void(loan, payments, payment.Id, "x", 0, Now));
    }

    [Fact]
    public void VoidingAnUnknownPaymentIsNotFound() =>
        AssertCode(
            LoansErrorCodes.CapitalPaymentNotFound,
            () => LoanCapitalOperations.Void(Loan(), [], Guid.NewGuid(), "x", 0, Now));

    [Fact]
    public void SettlingInsideADisbursementDayPeriodAnchoredOn12ChargesFromTheAnchor()
    {
        // Ancla 12, período 12/oct-11/nov (31 días). Saldar el 20/oct cobra 12/oct-19/oct = 8 días sobre 100.000.000:
        //   1.500.000 x 8 / 31 = 387.096,7742 -> 387.096,77
        var loan = Loan("2026-10-12", LoanCutoffRule.DisbursementDay, today: "2026-10-20");
        var payments = new List<LoanCapitalPayment>();

        var final = Add(loan, payments, 100_000_000m, "2026-10-20", today: "2026-10-20").FinalInstallment;

        Assert.NotNull(final);
        Assert.Equal((Date("2026-10-12"), 31), (final.PeriodStart, final.PeriodDays));
        Assert.Equal(387096.77m, final.Amount);
        Assert.Equal(LoanStatus.Active, loan.Status);
    }

    [Fact]
    public void SettlingInsideAnAnchor31PeriodThatStartedInTheShortMonthChargesFromThe28th()
    {
        // Ancla 31 desde el 31/ene. El 10/mar el período abierto es 28/feb-30/mar (31 días), no uno que arranque el 31/ene.
        // Saldar el 10/mar cobra 28/feb-9/mar = 1 + 9 = 10 días sobre 100.000.000: 1.500.000 x 10 / 31 = 483.870,9677 -> 483.870,97
        var loan = Loan("2026-01-31", LoanCutoffRule.DisbursementDay, today: "2026-03-10");
        var payments = new List<LoanCapitalPayment>();
        // El período 31/ene-27/feb ya cerró y se genera antes de abonar, como lo garantiza CapitalPaymentSupport.
        var closed = Assert.Single(LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-03-10"), Now));
        Assert.Equal(Date("2026-02-27"), closed.PeriodEnd);

        var final = Add(loan, payments, 100_000_000m, "2026-03-10", today: "2026-03-10").FinalInstallment;

        Assert.NotNull(final);
        Assert.Equal((Date("2026-02-28"), 31), (final.PeriodStart, final.PeriodDays));
        Assert.Equal(483870.97m, final.Amount);
    }

    [Fact]
    public void SettlingAfterAClosedDisbursementDayPeriodChargesOnlyTheOpenOne()
    {
        // Ancla 12, hoy 20/nov: el período 12/oct-11/nov ya cerró (1.500.000,00) y el abierto es 12/nov-11/dic (30 días).
        // Saldar el 20/nov cobra 12/nov-19/nov = 8 días: 1.500.000 x 8 / 30 = 400.000,00
        var loan = Loan("2026-10-12", LoanCutoffRule.DisbursementDay, today: "2026-11-20");
        var payments = new List<LoanCapitalPayment>();
        var closed = Assert.Single(LoanInstallmentGenerator.GenerateClosedPeriods(loan, payments, Date("2026-11-20"), Now));
        Assert.Equal(1500000.00m, closed.Amount);

        var final = Add(loan, payments, 100_000_000m, "2026-11-20", today: "2026-11-20").FinalInstallment;

        Assert.NotNull(final);
        Assert.Equal(Date("2026-11-12"), final.PeriodStart);
        Assert.Equal(400000.00m, final.Amount);
    }

    [Theory]
    [InlineData(LoanCutoffRule.EndOfMonth)]
    [InlineData(LoanCutoffRule.DisbursementDay)]
    public void SettlingOnTheDisbursementDayOfAPartialFirstPeriodCreatesNoInstallment(LoanCutoffRule rule)
    {
        // Desembolso el 10/oct: EndOfMonth abre el período el 1/oct (parcial: arranca el 10), DisbursementDay el 10/oct. En los
        // dos, saldar el 10/oct deja el rango 10/oct-9/oct vacío: nada que cobrar.
        var loan = Loan("2026-10-10", rule, today: "2026-10-10");
        var payments = new List<LoanCapitalPayment>();

        var outcome = Add(loan, payments, 100_000_000m, "2026-10-10", today: "2026-10-10");

        Assert.Null(outcome.FinalInstallment);
        Assert.Equal(LoanStatus.Settled, loan.Status);
        Assert.Equal(0m, loan.OutstandingPrincipal);
    }
}
