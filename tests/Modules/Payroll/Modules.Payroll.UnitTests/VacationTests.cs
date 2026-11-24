using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class VacationTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid Employee = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(2);

    private static readonly DateOnly Start = new(2026, 12, 14);
    private static readonly DateOnly End = new(2027, 1, 5);

    private static Vacation Qep() => Vacation.Create(
        VacationId.New(), Tenant, Company, Employee, VacationOrigin.Qep, Start, End, 15, new DateOnly(2027, 1, 6), null, [], T0);

    [Fact]
    public void CreateKeepsWhatItWasGiven()
    {
        var id = VacationId.New();
        var months = new[] { new ExternalVacationMonth(new DateOnly(2026, 12, 1), 900_000m, 36_000m, 36_000m, 0m) };

        var vacation = Vacation.Create(
            id, Tenant, Company, Employee, VacationOrigin.External, Start, End, 14, new DateOnly(2027, 1, 6), 2_800_000m, months, T0);

        Assert.Equal(id, vacation.Id);
        Assert.Equal((Tenant, Company, Employee), (vacation.TenantId, vacation.CompanyId, vacation.EmployeeId));
        Assert.Equal(VacationOrigin.External, vacation.Origin);
        Assert.Equal((Start, End), (vacation.StartDate, vacation.EndDate));
        Assert.Equal(14, vacation.BusinessDays);
        Assert.Equal(new DateOnly(2027, 1, 6), vacation.ReturnDate);
        Assert.Equal(2_800_000m, vacation.PreviousMonthIbc);
        Assert.Equal(months, vacation.ExternalMonths);
        Assert.Equal(1, vacation.Version);
        Assert.Equal((T0, T0), (vacation.CreatedAt, vacation.UpdatedAt));
    }

    [Fact]
    public void UpdateBumpsTheVersion()
    {
        var vacation = Qep();

        vacation.Update(VacationOrigin.Qep, Start, new DateOnly(2027, 1, 8), 17, new DateOnly(2027, 1, 12), 2_900_000m, [], T1);

        Assert.Equal(2, vacation.Version);
        Assert.Equal(new DateOnly(2027, 1, 8), vacation.EndDate);
        Assert.Equal(17, vacation.BusinessDays);
        Assert.Equal(2_900_000m, vacation.PreviousMonthIbc);
        Assert.Equal((T0, T1), (vacation.CreatedAt, vacation.UpdatedAt));
    }

    [Fact]
    public void TheMonthsListIsCopiedOnCreateAndUpdate()
    {
        // The caller keeps editing its own list (a request body); what was saved must not move with it.
        var typed = new List<ExternalVacationMonth> { new(new DateOnly(2026, 12, 1), 900_000m, 36_000m, 36_000m, 0m) };
        var vacation = Vacation.Create(
            VacationId.New(), Tenant, Company, Employee, VacationOrigin.External, Start, End, 14, new DateOnly(2027, 1, 6), 2_800_000m, typed, T0);
        typed.Add(new ExternalVacationMonth(new DateOnly(2027, 1, 1), null, 0m, 0m, 0m));
        Assert.Single(vacation.ExternalMonths);

        var updated = new List<ExternalVacationMonth> { new(new DateOnly(2026, 12, 1), null, 0m, 0m, 0m) };
        vacation.Update(VacationOrigin.External, Start, End, 14, new DateOnly(2027, 1, 6), 2_800_000m, updated, T1);
        updated.Clear();
        Assert.Single(vacation.ExternalMonths);
    }

    [Fact]
    public void QepCannotCarryExternalMonths()
    {
        var months = new[] { new ExternalVacationMonth(new DateOnly(2026, 12, 1), null, 0m, 0m, 0m) };

        Assert.Throws<ArgumentException>(() => Vacation.Create(
            VacationId.New(), Tenant, Company, Employee, VacationOrigin.Qep, Start, End, 15, new DateOnly(2027, 1, 6), null, months, T0));
        Assert.Throws<ArgumentException>(() =>
            Qep().Update(VacationOrigin.Qep, Start, End, 15, new DateOnly(2027, 1, 6), null, months, T1));
    }

    [Fact]
    public void ExternalNeedsThePreviousIbc() =>
        Assert.Throws<ArgumentException>(() => Vacation.Create(
            VacationId.New(), Tenant, Company, Employee, VacationOrigin.External, Start, End, 15, new DateOnly(2027, 1, 6), null, [], T0));

    [Fact]
    public void ExternalNeedsAtLeastOneBusinessDay() =>
        Assert.Throws<ArgumentException>(() => Vacation.Create(
            VacationId.New(), Tenant, Company, Employee, VacationOrigin.External, Start, End, 0, new DateOnly(2027, 1, 6), 2_800_000m, [], T0));

    [Fact]
    public void TheEndCannotPrecedeTheStart() =>
        Assert.Throws<ArgumentException>(() => Vacation.Create(
            VacationId.New(), Tenant, Company, Employee, VacationOrigin.Qep, End, Start, 15, new DateOnly(2027, 1, 6), null, [], T0));

    [Fact]
    public void OverlapsIsInclusive()
    {
        var vacation = Qep();

        Assert.True(vacation.Overlaps(new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 10)));
        Assert.True(vacation.Overlaps(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 14)));
        Assert.True(vacation.Overlaps(new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 21)));
        Assert.False(vacation.Overlaps(new DateOnly(2027, 1, 6), new DateOnly(2027, 1, 10)));
        Assert.False(vacation.Overlaps(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 13)));
    }
}
