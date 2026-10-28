using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class PosCustomerHandlersTests
{
    private static readonly string[] Customer = [PosPermissions.SaleCustomer];

    private static SearchPosCustomersHandler Search(PosTestBed bed, params string[] permissions) =>
        new(bed.Customers, bed.Context(permissions), new SearchPosCustomersValidator());

    private static CreatePosCustomerHandler Create(PosTestBed bed, params string[] permissions) =>
        new(bed.Customers, bed.Context(permissions), new CreatePosCustomerValidator());

    private static CreatePosCustomerCommand Valid() =>
        new(TenantId, "Laura Gómez", "CC", "1020304050", null);

    [Fact]
    public async Task SearchNeedsThePermissionAndTheTenant()
    {
        var bed = new PosTestBed();

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Search(bed, PosPermissions.SaleCreate).HandleAsync(new SearchPosCustomersQuery(TenantId, null), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Search(bed, Customer).HandleAsync(new SearchPosCustomersQuery(Guid.CreateVersion7(), null), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
        Assert.Empty(bed.Customers.Calls);
    }

    [Fact]
    public async Task SearchTrimsAndAsksForTwenty()
    {
        var bed = new PosTestBed();

        await Search(bed, Customer).HandleAsync(new SearchPosCustomersQuery(TenantId, "  Laura "), TestContext.Current.CancellationToken);
        await Search(bed, Customer).HandleAsync(new SearchPosCustomersQuery(TenantId, "   "), TestContext.Current.CancellationToken);

        Assert.Equal(["Search:Laura:20", "Search::20"], bed.Customers.Calls);
    }

    [Fact]
    public async Task SearchRejectsMoreThan120Characters()
    {
        var bed = new PosTestBed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Search(bed, Customer).HandleAsync(new SearchPosCustomersQuery(TenantId, new string('a', 121)), TestContext.Current.CancellationToken));

        Assert.Equal("Search", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Customers.Calls);
    }

    [Fact]
    public async Task CreateReturnsTheNewIncompleteRef()
    {
        var bed = new PosTestBed();

        var created = await Create(bed, Customer).HandleAsync(Valid() with { Phone = "3001234567" }, TestContext.Current.CancellationToken);

        Assert.Equal("Laura Gómez", created.Name);
        Assert.Equal("CC", created.IdentificationType);
        Assert.Equal("1020304050", created.IdentificationNumber);
        Assert.False(created.IsComplete);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task CreateWithATakenDocumentThrowsWithTheExistingCustomer()
    {
        var bed = new PosTestBed();
        var existing = bed.Customers.AddRef("Laura Pérez", "CC", "1020304050", complete: true, active: false);

        var error = await Assert.ThrowsAsync<PosCustomerIdentificationTakenException>(() =>
            Create(bed, Customer).HandleAsync(Valid() with { IdentificationNumber = " 1020304050 " }, TestContext.Current.CancellationToken));

        Assert.Equal("pos.customer.identification_taken", error.Code);
        var shown = Assert.IsType<PosCustomerOptionResponse>(error.ProblemExtensions["existing"]);
        Assert.Equal(existing.Id, shown.Id);
        Assert.False(shown.IsActive);
        Assert.DoesNotContain(bed.Customers.Calls, call => call.StartsWith("Create:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateNeedsThePermissionAndTheTenant()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Create(bed, PosPermissions.SaleCreate).HandleAsync(Valid(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Create(bed, Customer).HandleAsync(Valid() with { TenantId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));

        Assert.Empty(bed.Customers.Calls);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("IdentificationType")]
    [InlineData("IdentificationNumber")]
    [InlineData("Phone")]
    public async Task CreateValidatesEachField(string field)
    {
        var bed = new PosTestBed();
        var command = field switch
        {
            "Name" => Valid() with { Name = " " },
            "IdentificationType" => Valid() with { IdentificationType = "RUT" },
            "IdentificationNumber" => Valid() with { IdentificationNumber = new string('1', 33) },
            _ => Valid() with { Phone = new string('3', 33) },
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Create(bed, Customer).HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == field);
        Assert.Empty(bed.Customers.Calls);
    }
}
