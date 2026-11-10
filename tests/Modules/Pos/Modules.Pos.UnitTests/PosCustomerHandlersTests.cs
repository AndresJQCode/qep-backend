using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using FieldRequirement = Modules.Tenancy.Domain.FieldRequirement;
using PolicyOfCustomerFields = Modules.Tenancy.Domain.CustomerFieldPolicy;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class PosCustomerHandlersTests
{
    private static readonly string[] Customer = [PosPermissions.SaleCustomer];

    private static SearchPosCustomersHandler Search(PosTestBed bed, params string[] permissions) =>
        new(bed.Customers, bed.TenantDirectory, bed.Context(permissions), new SearchPosCustomersValidator());

    private static CreatePosCustomerHandler Create(PosTestBed bed, params string[] permissions) =>
        new(bed.Customers, bed.TenantDirectory, bed.Context(permissions), new CreatePosCustomerValidator());

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

    // Spec 2026-11-09 (final review): el selector lista a los incompletos sin documento sólo si el tenant no exige el
    // documento al facturar; sin fila regional rige la política por defecto.
    [Fact]
    public async Task SearchIncludesIncompleteWithoutDocumentOnlyWhenTheDocumentIsNotBilled()
    {
        var notBilled = new PosTestBed();
        notBilled.TenantDirectory.Regional = new("America/Bogota", "dd/MM/yyyy", "COP", "1.234,56",
            CustomerFieldPolicy: new PolicyOfCustomerFields(new FieldRequirement(false, false), FieldRequirement.Always));
        var billed = new PosTestBed();
        billed.TenantDirectory.Regional = WithDocumentOnSave(false);
        var noRegional = new PosTestBed();
        noRegional.TenantDirectory.Regional = null;

        foreach (var bed in new[] { notBilled, billed, noRegional })
        {
            await Search(bed, Customer).HandleAsync(new SearchPosCustomersQuery(TenantId, "Laura"), TestContext.Current.CancellationToken);
        }

        Assert.Equal([true], notBilled.Customers.SearchIncludedIncompleteWithoutDocument);
        Assert.Equal([false], billed.Customers.SearchIncludedIncompleteWithoutDocument);
        Assert.Equal([false], noRegional.Customers.SearchIncludedIncompleteWithoutDocument);
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

    private static Modules.Tenancy.Application.TenantRegionalSettings WithDocumentOnSave(bool onSave) =>
        new("America/Bogota", "dd/MM/yyyy", "COP", "1.234,56",
            CustomerFieldPolicy: new PolicyOfCustomerFields(new FieldRequirement(onSave, true), FieldRequirement.Always));

    [Fact]
    public async Task QuickCreateWithoutDocumentPassesWhenTheDocumentIsOptionalOnSave()
    {
        var bed = new PosTestBed();
        bed.TenantDirectory.Regional = WithDocumentOnSave(false);

        var created = await Create(bed, Customer).HandleAsync(
            Valid() with { IdentificationType = null, IdentificationNumber = null }, TestContext.Current.CancellationToken);

        Assert.Null(created.IdentificationType);
        Assert.Null(created.IdentificationNumber);
        Assert.Equal(["Create::"], bed.Customers.Calls);
    }

    [Fact]
    public async Task QuickCreateWithoutDocumentIsRequiredByDefault()
    {
        var bed = new PosTestBed();
        bed.TenantDirectory.Regional = null;

        var error = await Assert.ThrowsAsync<PosCustomerIdentificationException>(() =>
            Create(bed, Customer).HandleAsync(
                Valid() with { IdentificationType = null, IdentificationNumber = " " }, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_required", error.Code);
        Assert.Equal(["IdentificationNumber", "IdentificationType"], error.FieldErrors.Keys.Order(StringComparer.Ordinal));
        Assert.All(error.FieldErrors.Values, codes => Assert.Equal(["customers.customer.identification_required"], codes));
        Assert.Empty(bed.Customers.Calls);
    }

    [Fact]
    public async Task QuickCreateWithATypeButNoNumberNamesTheNumber()
    {
        var bed = new PosTestBed();
        bed.TenantDirectory.Regional = WithDocumentOnSave(false);

        var error = await Assert.ThrowsAsync<PosCustomerIdentificationException>(() =>
            Create(bed, Customer).HandleAsync(Valid() with { IdentificationNumber = null }, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_number_required", error.Code);
        var field = Assert.Single(error.FieldErrors);
        Assert.Equal("IdentificationNumber", field.Key);
        Assert.Equal(["customers.customer.identification_number_required"], field.Value);
        Assert.Equal(0, bed.TenantDirectory.Reads);
        Assert.Empty(bed.Customers.Calls);
    }

    [Fact]
    public async Task QuickCreateWithANumberButNoTypeIsIncomplete()
    {
        var bed = new PosTestBed();
        bed.TenantDirectory.Regional = WithDocumentOnSave(false);

        var error = await Assert.ThrowsAsync<PosCustomerIdentificationException>(() =>
            Create(bed, Customer).HandleAsync(Valid() with { IdentificationType = null }, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_incomplete", error.Code);
        var field = Assert.Single(error.FieldErrors);
        Assert.Equal("IdentificationType", field.Key);
        Assert.Equal(["customers.customer.identification_incomplete"], field.Value);
        Assert.Empty(bed.Customers.Calls);
    }

    [Fact]
    public async Task QuickCreateWithADocumentDoesNotReadThePolicy()
    {
        var bed = new PosTestBed();

        await Create(bed, Customer).HandleAsync(Valid(), TestContext.Current.CancellationToken);

        Assert.Equal(0, bed.TenantDirectory.Reads);
    }
}
