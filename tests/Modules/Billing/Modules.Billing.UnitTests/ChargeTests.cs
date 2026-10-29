using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class ChargeTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 3, 10);

    private static Charge NewCharge(DateOnly? due = null)
    {
        var service = BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100m, "COP",
            BillingCycle.Monthly, new DateOnly(2026, 1, 10), Now);
        return Charge.Generate(service, due ?? new DateOnly(2026, 3, 10), Now);
    }

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<BillingDomainException>(act).Code);

    [Fact]
    public void GenerateCopiesAmountAndCurrencyFromTheService()
    {
        var service = BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 99.5m, "USD",
            BillingCycle.Monthly, new DateOnly(2026, 1, 10), Now);

        var charge = Charge.Generate(service, new DateOnly(2026, 4, 10), Now);

        Assert.Equal(99.5m, charge.Amount);
        Assert.Equal("USD", charge.Currency);
        Assert.Equal(service.Id, charge.ServiceId);
        Assert.Equal(service.TenantId, charge.TenantId);
        Assert.Equal(service.CustomerId, charge.CustomerId);
        Assert.Equal(new DateOnly(2026, 4, 10), charge.DueDate);
        Assert.Equal(ChargeStatus.Pending, charge.Status);
        Assert.NotEqual(Guid.Empty, charge.Id);
    }

    [Fact]
    public void MarkPaidRequiresPendingAndANonFutureDate()
    {
        var charge = NewCharge();
        AssertCode(BillingErrorCodes.PaymentDateInFuture, () => charge.MarkPaid(Today.AddDays(1), null, Guid.NewGuid(), Today, Now));

        var member = Guid.NewGuid();
        charge.MarkPaid(Today, null, member, Today, Now);

        Assert.Equal(ChargeStatus.Paid, charge.Status);
        Assert.Equal(Today, charge.PaidOn);
        Assert.Equal(member, charge.PaidByMemberId);
        Assert.Equal(Now, charge.PaidAt);
        AssertCode(BillingErrorCodes.ChargeNotPending, () => charge.MarkPaid(Today, null, member, Today, Now));
    }

    [Fact]
    public void MarkPaidTrimsTheNoteAndKeepsNullForEmpty()
    {
        var trimmed = NewCharge();
        trimmed.MarkPaid(Today, "  por Nequi ", Guid.NewGuid(), Today, Now);
        Assert.Equal("por Nequi", trimmed.PaymentNote);

        var blank = NewCharge();
        blank.MarkPaid(Today, "   ", Guid.NewGuid(), Today, Now);
        Assert.Null(blank.PaymentNote);

        var tooLong = NewCharge();
        AssertCode(BillingErrorCodes.PaymentNoteTooLong, () => tooLong.MarkPaid(Today, new string('x', 1001), Guid.NewGuid(), Today, Now));
        Assert.Equal(ChargeStatus.Pending, tooLong.Status);
    }

    [Fact]
    public void UpdatePaymentRequiresPaid()
    {
        var charge = NewCharge();
        AssertCode(BillingErrorCodes.ChargeNotPaid, () => charge.UpdatePayment(Today, null, Today, Now));

        charge.MarkPaid(Today.AddDays(-2), null, Guid.NewGuid(), Today, Now);
        charge.UpdatePayment(Today.AddDays(-1), " ok ", Today, Now);

        Assert.Equal(Today.AddDays(-1), charge.PaidOn);
        Assert.Equal("ok", charge.PaymentNote);
        AssertCode(BillingErrorCodes.PaymentDateInFuture, () => charge.UpdatePayment(Today.AddDays(1), null, Today, Now));
    }

    [Fact]
    public void IsOverdueOnlyWhilePendingAndPastDue()
    {
        var charge = NewCharge(new DateOnly(2026, 3, 10));

        Assert.True(charge.IsOverdue(new DateOnly(2026, 3, 11)));
        Assert.False(charge.IsOverdue(new DateOnly(2026, 3, 10)));

        charge.MarkPaid(Today, null, Guid.NewGuid(), Today, Now);
        Assert.False(charge.IsOverdue(new DateOnly(2026, 3, 11)));
    }

    [Fact]
    public void CancelOnlyAPendingCharge()
    {
        var charge = NewCharge();
        charge.Cancel(Now);
        Assert.Equal(ChargeStatus.Cancelled, charge.Status);
        AssertCode(BillingErrorCodes.ChargeNotPending, () => charge.Cancel(Now));

        var paid = NewCharge();
        paid.MarkPaid(Today, null, Guid.NewGuid(), Today, Now);
        AssertCode(BillingErrorCodes.ChargeNotPending, () => paid.Cancel(Now));
    }
}
