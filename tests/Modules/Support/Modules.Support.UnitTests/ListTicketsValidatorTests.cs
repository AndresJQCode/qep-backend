using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Los filtros de la bandeja: estados por nombre exacto (varios separados por coma), responsable <c>me</c> |
/// <c>unassigned</c> | id, plazo <c>overdue</c> | <c>dueSoon</c>, y página de 1 a 100.</summary>
public sealed class ListTicketsValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static ListTicketsQuery Query(
        string? status = null,
        string? assignee = null,
        string? due = null,
        int page = 1,
        int pageSize = ListTicketsQuery.DefaultPageSize,
        string? priority = null,
        string? sort = null,
        string? kind = null,
        string? createdBy = null) =>
        new(Tenant, status, null, assignee, due, null, priority, sort, page, pageSize, kind, createdBy);

    private static List<string> Errors(ListTicketsQuery query) =>
        new ListTicketsValidator().Validate(query).Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void ValidFiltersPass()
    {
        Assert.Empty(Errors(Query()));
        Assert.Empty(Errors(Query(status: "New,Open", assignee: "me", due: "overdue", pageSize: 100)));
        Assert.Empty(Errors(Query(status: "WaitingOnCustomer", assignee: "unassigned", due: "dueSoon")));
        Assert.Empty(Errors(Query(assignee: Guid.CreateVersion7().ToString())));
    }

    [Fact]
    public void APageSizeAbove100Fails()
    {
        Assert.Equal(["PageSize"], Errors(Query(pageSize: 101)));
        Assert.Equal(["PageSize"], Errors(Query(pageSize: 0)));
        Assert.Equal(["Page"], Errors(Query(page: 0)));
    }

    [Fact]
    public void AnUnknownStatusFails()
    {
        Assert.Equal(["Status"], Errors(Query(status: "Pending")));
        Assert.Equal(["Status"], Errors(Query(status: "New,open")));
        Assert.Equal(["Status"], Errors(Query(status: "New,,Open")));
    }

    [Fact]
    public void AnUnknownDueFilterFails()
    {
        Assert.Equal(["Due"], Errors(Query(due: "late")));
        Assert.Equal(["Due"], Errors(Query(due: "DueSoon")));
    }

    [Fact]
    public void AnUnknownAssigneeFails()
    {
        Assert.Equal(["Assignee"], Errors(Query(assignee: "nobody")));
    }

    [Fact]
    public void PrioritiesAreExactNamesAndSortIsReceivedOrPriority()
    {
        Assert.Empty(Errors(Query(priority: "High,Urgent", sort: "priority")));
        Assert.Empty(Errors(Query(priority: "Low", sort: "received")));
        Assert.Equal(["Priority"], Errors(Query(priority: "High,urgent")));
        Assert.Equal(["Priority"], Errors(Query(priority: "2")));
        Assert.Equal(["Sort"], Errors(Query(sort: "Priority")));
    }

    [Fact]
    public void KindMustBeExternalInternalOrAll()
    {
        Assert.Empty(Errors(Query(kind: "external")));
        Assert.Empty(Errors(Query(kind: "internal")));
        Assert.Empty(Errors(Query(kind: "all")));
        Assert.Equal(["Kind"], Errors(Query(kind: "tasks")));
        Assert.Equal(["Kind"], Errors(Query(kind: "Internal")));
    }

    [Fact]
    public void CreatedByMustBeMe()
    {
        Assert.Empty(Errors(Query(createdBy: "me")));
        Assert.Equal(["CreatedBy"], Errors(Query(createdBy: "someone")));
        Assert.Equal(["CreatedBy"], Errors(Query(createdBy: Guid.CreateVersion7().ToString())));
    }
}
