using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class RecurringConceptTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid Employee = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid Concept = Guid.Parse("00000000-0000-0000-0002-000000000004");
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private static RecurringConcept Make(decimal? cap = 1_200_000m, decimal amount = 100_000m) =>
        RecurringConcept.Create(
            RecurringConceptId.New(), Tenant, Company, Employee, Concept, RecurringMode.Fixed, amount, false, cap, T0);

    private static string CodeOf(Action action) => Assert.Throws<PayrollDomainException>(action).Code;

    [Fact]
    public void ANewOneIsActiveAtVersionOne()
    {
        var recurring = Make();

        Assert.True(recurring.Active);
        Assert.Equal(1, recurring.Version);
        Assert.Equal(RecurringStatus.Active, recurring.StatusFor(0m));
    }

    [Fact]
    public void ACapReachedIsExhausted()
    {
        var recurring = Make(cap: 1_200_000m);

        Assert.Equal(RecurringStatus.Active, recurring.StatusFor(1_199_999m));
        Assert.Equal(RecurringStatus.Exhausted, recurring.StatusFor(1_200_000m));
        Assert.Equal(RecurringStatus.Exhausted, recurring.StatusFor(1_300_000m));
    }

    [Fact]
    public void WithoutACapItIsNeverExhausted() =>
        Assert.Equal(RecurringStatus.Active, Make(cap: null).StatusFor(999_999_999m));

    [Fact]
    public void AnInactiveOneIsInactiveEvenUnderTheCap()
    {
        var recurring = Make();
        recurring.Deactivate(T0.AddMinutes(1));

        Assert.Equal(RecurringStatus.Inactive, recurring.StatusFor(0m));
        Assert.Equal(RecurringStatus.Inactive, recurring.StatusFor(5_000_000m));
        Assert.Equal(2, recurring.Version);
    }

    [Fact]
    public void ActivatingTwiceIsRejected()
    {
        var recurring = Make();

        Assert.Equal(PayrollErrorCodes.RecurringAlreadyActive, CodeOf(() => recurring.Activate(T0)));
    }

    [Fact]
    public void DeactivatingTwiceIsRejected()
    {
        var recurring = Make();
        recurring.Deactivate(T0);

        Assert.Equal(PayrollErrorCodes.RecurringAlreadyInactive, CodeOf(() => recurring.Deactivate(T0)));
    }

    [Fact]
    public void UpdatingChangesTheTermsAndBumpsTheVersion()
    {
        var recurring = Make();

        recurring.Update(RecurringMode.Proportional, 80_000m, true, null, T0.AddMinutes(2));

        Assert.Equal(RecurringMode.Proportional, recurring.Mode);
        Assert.Equal(80_000m, recurring.Amount);
        Assert.True(recurring.SalaryConstitutive);
        Assert.Null(recurring.Cap);
        Assert.Equal(2, recurring.Version);
        Assert.Equal(T0.AddMinutes(2), recurring.UpdatedAt);
    }

    [Fact]
    public void ACapBelowTheAmountIsAProgrammingErrorTheValidatorCatchesFirst() =>
        Assert.Throws<ArgumentException>(() => Make(cap: 50_000m, amount: 100_000m));
}
