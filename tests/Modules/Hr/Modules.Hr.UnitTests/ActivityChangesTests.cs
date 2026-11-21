using Modules.Hr.Application;
using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

/// <summary>El antes y el después del historial de acciones: puros, sin base. Referencias por el nombre que tenían al escribirse.</summary>
public sealed class ActivityChangesTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static string Text(System.Text.Json.JsonElement? value) => value!.Value.ToString();

    [Fact]
    public void OnlyChangedFieldsAreListed()
    {
        var before = Profile();
        var after = before with { LastName = "Gómez", Phone = "+573001234567" };

        var changes = ActivityChanges.Diff(before, after);

        Assert.Equal(["lastName", "phone"], changes.Select(change => change.Field));
        Assert.Equal("Pérez", Text(changes[0].Before));
        Assert.Equal("Gómez", Text(changes[0].After));
        Assert.Null(changes[1].Before);
        Assert.Empty(ActivityChanges.Diff(before, before with { }));
    }

    [Fact]
    public void ACountryChangeIsPersonalAndNotSensitive()
    {
        var before = Profile();

        var change = Assert.Single(ActivityChanges.Diff(before, before with { Country = "VE" }));

        Assert.Equal("country", change.Field);
        Assert.Equal("CO", Text(change.Before));
        Assert.Equal("VE", Text(change.After));
        Assert.False(change.Sensitive);
    }

    [Fact]
    public void PaymentChangesAreSensitive()
    {
        var payment = new PaymentDetails(PaymentMethod.BankTransfer, "Bancolombia", BankAccountType.Savings, "12345678901");

        var created = ActivityChanges.Diff(null, payment);
        var cleared = ActivityChanges.Diff(payment, null);

        Assert.Equal(["paymentMethod", "bankName", "bankAccountType", "bankAccountNumber"], created.Select(change => change.Field));
        Assert.All(created, change => Assert.True(change.Sensitive));
        Assert.All(cleared, change => Assert.Null(change.After));
        Assert.Empty(ActivityChanges.Diff(payment, payment with { }));
    }

    [Fact]
    public void AssignmentChangesUseNamesAtWriteTime()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;
        employee.AddAssignment(
            contract.Id,
            AssignmentRecordId.New(),
            new AssignmentEntry(new DateOnly(2026, 12, 15), ManagerId, LogisticsId, MainSite.Id, Warehouse.Id),
            null,
            Later);
        var previous = contract.Assignments.OrderBy(record => record.EffectiveDate).First();
        var next = contract.Assignments.OrderBy(record => record.EffectiveDate).Last();
        var names = new Dictionary<Guid, string>
        {
            [AnalystId.Value] = "Analista",
            [ManagerId.Value] = "Gerente",
            [LogisticsId.Value] = "Logística",
            [MainSite.Id.Value] = "Principal",
            [Warehouse.Id.Value] = "Bodega",
        };

        var changes = ActivityChanges.AssignmentCreated(previous, next, names);

        Assert.Equal(["effectiveDate", "position", "area"], changes.Select(change => change.Field));
        var position = changes.Single(change => change.Field == "position");
        Assert.Equal("Analista", Text(position.Before));
        Assert.Equal("Gerente", Text(position.After));
        Assert.Null(changes.Single(change => change.Field == "area").Before);
        Assert.Equal("Logística", Text(changes.Single(change => change.Field == "area").After));
        Assert.All(changes, change => Assert.True(change.Sensitive));
    }

    [Fact]
    public void ASalaryRaiseListsTheCurrentAndTheNewAmount()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;
        employee.AddSalary(
            contract.Id, SalaryRecordId.New(), new SalaryEntry(new DateOnly(2026, 12, 1), 2_800_000m, false, false), Floor2026, null, Later);
        var current = contract.Salaries.OrderBy(record => record.EffectiveDate).First();
        var next = contract.Salaries.OrderBy(record => record.EffectiveDate).Last();

        var changes = ActivityChanges.SalaryCreated(current, next);

        Assert.Equal(["amount", "effectiveDate"], changes.Select(change => change.Field));
        Assert.Equal("2500000", Text(changes[0].Before));
        Assert.Equal("2800000", Text(changes[0].After));
        Assert.Equal("2026-12-01", Text(changes[1].After));
        Assert.All(changes, change => Assert.True(change.Sensitive));
    }

    // El estado anterior de una reincorporación es el que tenía el empleado: Retired si lo terminaron, Deactivated si lo desactivaron.
    [Theory]
    [InlineData(EmployeeStatus.Retired)]
    [InlineData(EmployeeStatus.Deactivated)]
    public void ReincorporationRecordsTheRealPreviousStatus(EmployeeStatus previous)
    {
        var contract = Hire().ActiveContract!;

        var changes = ActivityChanges.Reincorporated(previous, contract, new Dictionary<Guid, string>());

        var status = changes.Single(change => change.Field == "status");
        Assert.Equal(previous.ToString(), Text(status.Before));
        Assert.Equal("Active", Text(status.After));
        Assert.False(status.Sensitive);
    }

    [Fact]
    public void TerminationKeepsTheStatusVisibleAndHidesTheReason()
    {
        var changes = ActivityChanges.Terminated(TerminationReason.Resignation, new DateOnly(2026, 11, 30));

        Assert.False(changes.Single(change => change.Field == "status").Sensitive);
        Assert.True(changes.Single(change => change.Field == "reason").Sensitive);
        Assert.True(changes.Single(change => change.Field == "date").Sensitive);
    }
}
