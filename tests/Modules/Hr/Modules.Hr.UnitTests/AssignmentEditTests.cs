using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

/// <summary>
/// Editar y eliminar registros de cargo y lugar (spec 2026-11-26, «Vecinos»). <c>Hire()</c> arranca el 2026-01-15 en
/// <c>Warehouse</c> con <c>AnalystId</c>.
/// </summary>
public sealed class AssignmentEditTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static AssignmentRecord Add(Employee employee, DateOnly date, PositionId position, Workplace workplace)
    {
        var id = AssignmentRecordId.New();
        employee.AddAssignment(
            employee.ActiveContract!.Id,
            id,
            new AssignmentEntry(date, position, null, workplace.SiteId, workplace.Id),
            null,
            Later);
        return employee.ActiveContract.Assignments.Single(record => record.Id == id);
    }

    private static AssignmentEntry Entry(DateOnly date, PositionId position, Workplace workplace) =>
        new(date, position, null, workplace.SiteId, workplace.Id);

    private static void Rewrite(Employee employee, AssignmentRecord record, AssignmentEntry entry, bool confirm = true) =>
        employee.RewriteAssignment(employee.ActiveContract!.Id, record.Id, entry, confirm, Later);

    private static AssignmentRecord Remove(Employee employee, AssignmentRecord record) =>
        employee.RemoveAssignment(employee.ActiveContract!.Id, record.Id, Later);

    private static AssignmentRecord First(Employee employee) =>
        employee.ActiveContract!.Assignments.Single(record => record.EffectiveDate == new DateOnly(2026, 1, 15));

    [Fact]
    public void RewritingWithoutConfirmationIsRejected()
    {
        var employee = Hire();
        var first = First(employee);

        var exception = Rejects(() => Rewrite(employee, first, Entry(first.EffectiveDate, ManagerId, Warehouse), confirm: false));

        Assert.Equal("hr.assignment.rewrite_unconfirmed", exception.Code);
    }

    [Fact]
    public void TheFirstRecordKeepsItsDate()
    {
        var employee = Hire();
        var first = First(employee);

        var exception = Rejects(() => Rewrite(employee, first, Entry(new DateOnly(2026, 1, 16), AnalystId, Warehouse)));

        Assert.Equal("hr.assignment.first_record_date_fixed", exception.Code);
    }

    [Fact]
    public void TheFirstRecordChangesItsPosition()
    {
        var employee = Hire();
        var first = First(employee);
        var version = employee.Version;

        Rewrite(employee, first, Entry(first.EffectiveDate, ManagerId, Warehouse));

        Assert.Equal(ManagerId, first.PositionId);
        Assert.Equal(new DateOnly(2026, 1, 15), first.EffectiveDate);
        Assert.Equal(version + 1, employee.Version);
    }

    [Fact]
    public void ANewDateMustStayBetweenItsNeighbours()
    {
        var employee = Hire();
        var middle = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);
        Add(employee, new DateOnly(2026, 5, 20), AnalystId, Warehouse);

        Assert.Equal(
            "hr.assignment.effective_date_out_of_order",
            Rejects(() => Rewrite(employee, middle, Entry(new DateOnly(2026, 5, 20), ManagerId, Warehouse))).Code);
        Assert.Equal(
            "hr.assignment.effective_date_out_of_order",
            Rejects(() => Rewrite(employee, middle, Entry(new DateOnly(2026, 1, 15), ManagerId, Warehouse))).Code);

        Rewrite(employee, middle, Entry(new DateOnly(2026, 4, 1), ManagerId, Warehouse));

        Assert.Equal(new DateOnly(2026, 4, 1), middle.EffectiveDate);
    }

    [Fact]
    public void ARewriteEqualToThePreviousIsUnchanged()
    {
        var employee = Hire();
        var middle = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);

        var exception = Rejects(() => Rewrite(employee, middle, Entry(new DateOnly(2026, 3, 10), AnalystId, Warehouse)));

        Assert.Equal("hr.assignment.unchanged", exception.Code);
    }

    [Fact]
    public void ARewriteEqualToTheNextIsUnchanged()
    {
        var employee = Hire();
        var first = First(employee);
        Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);

        var exception = Rejects(() => Rewrite(employee, first, Entry(first.EffectiveDate, ManagerId, Warehouse)));

        Assert.Equal("hr.assignment.unchanged", exception.Code);
    }

    [Fact]
    public void APlaceChangeOffTheFirstIsRejectedOnEitherPair()
    {
        var employee = Hire();
        var middle = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);

        Assert.Equal(
            "hr.assignment.site_change_not_first_of_month",
            Rejects(() => Rewrite(employee, middle, Entry(new DateOnly(2026, 3, 10), ManagerId, OtherWarehouse))).Code);

        // El editado queda el día 1, pero el siguiente (05-20) pasaría a cambiar de lugar un día distinto del 1.
        Add(employee, new DateOnly(2026, 5, 20), AnalystId, Warehouse);
        Assert.Equal(
            "hr.assignment.site_change_not_first_of_month",
            Rejects(() => Rewrite(employee, middle, Entry(new DateOnly(2026, 3, 1), ManagerId, OtherWarehouse))).Code);
    }

    [Fact]
    public void TheFirstRecordIsNotDeletable()
    {
        var employee = Hire();
        Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);

        var exception = Rejects(() => Remove(employee, First(employee)));

        Assert.Equal("hr.assignment.first_record_not_deletable", exception.Code);
    }

    [Fact]
    public void DeletingAMiddleRecordChecksThePreviousAgainstTheNext()
    {
        var employee = Hire();
        var middle = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);
        Add(employee, new DateOnly(2026, 5, 20), AnalystId, Warehouse);

        var exception = Rejects(() => Remove(employee, middle));

        Assert.Equal("hr.assignment.unchanged", exception.Code);
        Assert.Equal(3, employee.ActiveContract!.Assignments.Count);
    }

    [Fact]
    public void DeletingTheLastRecordReturnsIt()
    {
        var employee = Hire();
        var last = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);
        var version = employee.Version;

        var removed = Remove(employee, last);

        Assert.Same(last, removed);
        Assert.DoesNotContain(employee.ActiveContract!.Assignments, record => record.Id == last.Id);
        Assert.Equal(version + 1, employee.Version);
    }

    [Fact]
    public void CanRemoveAssignmentAnswersWithoutMutating()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;
        var middle = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);
        var last = Add(employee, new DateOnly(2026, 5, 20), AnalystId, Warehouse);
        var count = contract.Assignments.Count;
        var version = employee.Version;

        Assert.False(contract.CanRemoveAssignment(First(employee).Id));
        Assert.False(contract.CanRemoveAssignment(middle.Id));
        Assert.True(contract.CanRemoveAssignment(last.Id));

        Assert.Equal(count, contract.Assignments.Count);
        Assert.Equal(version, employee.Version);
    }

    [Fact]
    public void EditsOnANonActiveContractAreRejected()
    {
        var employee = Hire();
        var contract = employee.ActiveContract!;
        var last = Add(employee, new DateOnly(2026, 3, 10), ManagerId, Warehouse);
        employee.Terminate(contract.Id, TerminationReason.Resignation, new DateOnly(2026, 11, 30), null, Later);

        Assert.Equal("hr.contract.not_active", Rejects(() => employee.RewriteAssignment(
            contract.Id, last.Id, Entry(last.EffectiveDate, AnalystId, Warehouse), true, Later)).Code);
        Assert.Equal("hr.contract.not_active", Rejects(() => employee.RemoveAssignment(contract.Id, last.Id, Later)).Code);
    }

    [Fact]
    public void AnUnknownRecordIsNotFound()
    {
        var employee = Hire();
        var contractId = employee.ActiveContract!.Id;

        Assert.Equal("hr.assignment.not_found", Rejects(() => employee.RewriteAssignment(
            contractId, AssignmentRecordId.New(), Entry(new DateOnly(2026, 3, 10), ManagerId, Warehouse), true, Later)).Code);
        Assert.Equal("hr.assignment.not_found", Rejects(() => employee.RemoveAssignment(
            contractId, AssignmentRecordId.New(), Later)).Code);
    }
}
