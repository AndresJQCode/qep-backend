using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>CD-04 (spec 2026-10-24 §3.3): las anomalías de clientes. Autoriza antes de validar, devuelve los conteos de las dos
/// clases aunque estén en cero, recorta la página y corta los grupos de más de 50.</summary>
public sealed class CustomerAnomaliesHandlerTests
{
    private readonly FakeCustomerDuplicateQueries _queries = new();

    private ListCustomerAnomaliesHandler CreateHandler(Guid? tenant = null, params string[] permissions) =>
        new(_queries, new ListCustomerAnomaliesValidator(),
            new Context(tenant ?? TenantId, UserA, permissions.Length == 0 ? [CustomersPermissions.CustomerRead] : permissions));

    private static Customer NewCustomer(string name) =>
        Customer.Create(
            CustomerId.New(), TenantId, $"MED-{Guid.NewGuid():N}"[..12], name, null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "92-1396892" },
            new CustomerContactInfo
            {
                Phone = "304 354 9660",
                Email = "compras@verde.co",
                Address = "Calle 10 # 45-12",
                Country = Customer.ColombiaCountryCode,
                CityId = Guid.NewGuid()
            },
            new CustomerCommercialInfo { ClassificationId = new ClientClassificationId(Guid.NewGuid()) },
            Now);

    [Fact]
    public async Task CountsCarryBothKindsEvenAtZero()
    {
        var response = await CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "SharedPhone", 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal(
            [("SharedPhone", 0), ("SharedIdentificationNumber", 0)],
            response.Counts.Select(count => (count.Kind, count.Groups)).ToArray());
        Assert.Empty(response.Groups);
        Assert.Equal(0, response.Total);
    }

    [Fact]
    public async Task AnUnknownKindIs422OnKind()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "Phone", 1, 20), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.PropertyName == "Kind");
        Assert.Empty(_queries.AnomalyListCalls);
    }

    [Fact]
    public async Task AMissingKindIs422OnKind()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, null, 1, 20), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.PropertyName == "Kind");
    }

    [Fact]
    public async Task PageSizeIsClampedToFifty()
    {
        var response = await CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "SharedPhone", 3, 500), TestContext.Current.CancellationToken);

        var call = Assert.Single(_queries.AnomalyListCalls);
        Assert.Equal((TenantId, CustomerAnomalyKind.SharedPhone, 100, 50), call);
        Assert.Equal(50, response.PageSize);
        Assert.Equal(3, response.Page);
    }

    [Fact]
    public async Task AZeroPageAndPageSizeFallBackToTheDefaults()
    {
        var response = await CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "SharedPhone", 0, 0), TestContext.Current.CancellationToken);

        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
        Assert.Equal((TenantId, CustomerAnomalyKind.SharedPhone, 0, 20), Assert.Single(_queries.AnomalyListCalls));
    }

    [Fact]
    public async Task AGroupWithMoreThanFiftyIsCutAndFlagged()
    {
        _queries.AnomalyGroups[CustomerAnomalyKind.SharedIdentificationNumber] = [new CustomerAnomalyGroupRow("921396892", 60)];
        _queries.AnomalyMembers["921396892"] = Enumerable.Range(0, 60).Select(i => NewCustomer($"Cliente {i}")).ToList();

        var response = await CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "SharedIdentificationNumber", 1, 20), TestContext.Current.CancellationToken);

        var group = Assert.Single(response.Groups);
        Assert.Equal(50, group.Customers.Count);
        Assert.True(group.Truncated);
        Assert.Equal(1, response.Total);
        Assert.Equal(1, response.Counts.Single(count => count.Kind == "SharedIdentificationNumber").Groups);
        Assert.Equal(51, Assert.Single(_queries.AnomalyMemberCalls).PerGroup + 1);
    }

    [Fact]
    public async Task AGroupOfExactlyFiftyIsNotTruncatedAndMapsTheCustomer()
    {
        _queries.AnomalyGroups[CustomerAnomalyKind.SharedPhone] = [new CustomerAnomalyGroupRow("+573043549660", 50)];
        var members = Enumerable.Range(0, 50).Select(i => NewCustomer($"Cliente {i}")).ToList();
        _queries.AnomalyMembers["+573043549660"] = members;

        var response = await CreateHandler().HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "SharedPhone", 1, 20), TestContext.Current.CancellationToken);

        var group = Assert.Single(response.Groups);
        Assert.False(group.Truncated);
        Assert.Equal("+573043549660", group.Key);
        var first = group.Customers[0];
        Assert.Equal(members[0].Id.Value, first.Id);
        Assert.Equal("NIT", first.IdentificationType);
        Assert.Equal("92-1396892", first.IdentificationNumber);
        Assert.Equal("304 354 9660", first.Phone);
        Assert.True(first.IsComplete);
        Assert.True(first.IsActive);
    }

    [Fact]
    public async Task AnotherTenantIs403BeforeValidating()
    {
        await Assert.ThrowsAsync<RequestForbiddenException>(() => CreateHandler(OtherTenantId).HandleAsync(
            new ListCustomerAnomaliesQuery(TenantId, "NO-EXISTE", 1, 20), TestContext.Current.CancellationToken));
        Assert.Empty(_queries.AnomalyListCalls);
    }
}
