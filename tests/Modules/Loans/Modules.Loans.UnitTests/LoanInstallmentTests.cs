using Modules.Loans.Domain;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class LoanInstallmentTests
{
    private static readonly DateOnly Today = Date("2026-11-10");

    private static LoanInstallment NewInstallment()
    {
        var loan = Loan();
        var period = new LoanPeriod(Date("2026-10-01"), Date("2026-10-31"));
        var segments = LoanInterestCalculator.Segments(period, period.Start, period.End, 100_000_000m, [], 1.5m);
        return LoanInstallment.Generate(loan, period.Start, period.End, period.Days, segments, Now);
    }

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<LoansDomainException>(act).Code);

    [Fact]
    public void GenerateCopiesTheLoanAndSumsTheSegments()
    {
        var loan = Loan();
        var period = new LoanPeriod(Date("2026-10-01"), Date("2026-10-31"));
        var segments = LoanInterestCalculator.Segments(
            period, period.Start, period.End, 100_000_000m, [new CapitalChange(Date("2026-10-15"), 60_000_000m)], 1.5m);

        var installment = LoanInstallment.Generate(loan, period.Start, period.End, period.Days, segments, Now);

        Assert.Equal(1170967.74m, installment.Amount);
        Assert.Equal(31, installment.PeriodDays);
        Assert.Equal(loan.Id, installment.LoanId);
        Assert.Equal(loan.TenantId, installment.TenantId);
        Assert.Equal("COP", installment.Currency);
        Assert.Equal(LoanInstallmentStatus.Pending, installment.Status);
        Assert.False(installment.IsImported);
        Assert.NotEqual(Guid.Empty, installment.Id);
        Assert.Equal(2, installment.Segments.Count);
        Assert.Equal([1, 2], installment.Segments.Select(segment => segment.Ordinal));
        Assert.All(installment.Segments, segment => Assert.Equal(installment.Id, segment.InstallmentId));
        Assert.Equal(677419.35m, installment.Segments[0].Interest);
    }

    [Fact]
    public void MarkPaidRequiresPendingAndANonFutureDate()
    {
        var installment = NewInstallment();
        AssertCode(LoansErrorCodes.PaymentDateInFuture, () => installment.MarkPaid(Today.AddDays(1), null, Guid.NewGuid(), Today, Now));

        var member = Guid.NewGuid();
        installment.MarkPaid(Today, null, member, Today, Now);

        Assert.Equal(LoanInstallmentStatus.Paid, installment.Status);
        Assert.Equal(Today, installment.PaidOn);
        Assert.Equal(member, installment.PaidByMemberId);
        Assert.Equal(Now, installment.PaidAt);
        AssertCode(LoansErrorCodes.InstallmentNotPending, () => installment.MarkPaid(Today, null, member, Today, Now));
    }

    [Fact]
    public void MarkPaidTrimsTheNoteAndKeepsNullForEmpty()
    {
        var trimmed = NewInstallment();
        trimmed.MarkPaid(Today, "  por Nequi ", Guid.NewGuid(), Today, Now);
        Assert.Equal("por Nequi", trimmed.PaymentNote);

        var blank = NewInstallment();
        blank.MarkPaid(Today, "   ", Guid.NewGuid(), Today, Now);
        Assert.Null(blank.PaymentNote);

        var tooLong = NewInstallment();
        AssertCode(LoansErrorCodes.PaymentNoteTooLong, () => tooLong.MarkPaid(Today, new string('x', 1001), Guid.NewGuid(), Today, Now));
        Assert.Equal(LoanInstallmentStatus.Pending, tooLong.Status);
    }

    [Fact]
    public void UpdatePaymentRequiresPaid()
    {
        var installment = NewInstallment();
        AssertCode(LoansErrorCodes.InstallmentNotPaid, () => installment.UpdatePayment(Today, null, Today, Now));

        installment.MarkPaid(Today.AddDays(-2), null, Guid.NewGuid(), Today, Now);
        installment.UpdatePayment(Today.AddDays(-1), " ok ", Today, Now);

        Assert.Equal(Today.AddDays(-1), installment.PaidOn);
        Assert.Equal("ok", installment.PaymentNote);
        AssertCode(LoansErrorCodes.PaymentDateInFuture, () => installment.UpdatePayment(Today.AddDays(1), null, Today, Now));
    }

    [Fact]
    public void RemovingThePaymentReturnsTheInstallmentToPending()
    {
        var installment = NewInstallment();
        AssertCode(LoansErrorCodes.InstallmentNotPaid, installment.RemovePayment);

        installment.MarkPaid(Today, "nota", Guid.NewGuid(), Today, Now);
        installment.RemovePayment();

        Assert.Equal(LoanInstallmentStatus.Pending, installment.Status);
        Assert.Null(installment.PaidOn);
        Assert.Null(installment.PaymentNote);
        Assert.Null(installment.PaidByMemberId);
        Assert.Null(installment.PaidAt);
    }

    [Fact]
    public void EveryPaymentChangeBumpsTheVersion()
    {
        var installment = NewInstallment();
        var version = installment.Version;

        installment.MarkPaid(Today, null, Guid.NewGuid(), Today, Now);
        installment.UpdatePayment(Today, "x", Today, Now);
        installment.RemovePayment();

        Assert.Equal(version + 3, installment.Version);
    }
}
