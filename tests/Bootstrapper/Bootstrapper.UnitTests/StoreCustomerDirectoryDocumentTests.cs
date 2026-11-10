using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Store.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers → Store. Un cliente cuyo documento es un NIF español se encuentra por documento: la llave del candidato
/// acepta los siete códigos, no sólo los cuatro colombianos.
/// </summary>
public sealed class StoreCustomerDirectoryDocumentTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Customer Spaniard() =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI00000142", "Lucía Gómez", null, null,
            new CustomerIdentification { Type = IdentificationType.Nif, Number = "12345678Z" }, null,
            new CustomerContactInfo
            {
                Phone = "+34 600 111 222",
                Address = "Calle Gran Via 28",
                Country = "ES",
                CityName = "Madrid"
            },
            new CustomerCommercialInfo(), Now);

    [Fact]
    public async Task ACandidateWithANifIsFoundByItsDocument()
    {
        var customer = Spaniard();
        var customers = new FakeCustomerRepository(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;
        var directory = new StoreCustomerDirectory(customers, new FakeCustomerProvisioning());

        var found = await directory.FindCandidatesAsync(TenantId, "+34600111222", "nif", " 12345678Z ", Ct);

        var candidate = Assert.Single(found);
        Assert.Equal(customer.Id.Value, candidate.Id);
        Assert.Equal("NIF", candidate.IdentificationType);
        Assert.Equal(["Document"], candidate.MatchedBy);
    }

    [Fact]
    public async Task AKnownKeyWithANifMatchesByDocument()
    {
        var customer = Spaniard();
        var customers = new FakeCustomerRepository(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;
        var directory = new StoreCustomerDirectory(customers, new FakeCustomerProvisioning());

        var known = await directory.KnownAsync(TenantId, [("+34600111222", "NIF", "12345678Z"), ("+34600333444", "NIF", "99999999R")], Ct);

        Assert.Equal([0], known.ToArray());
    }

    private static StoreNewCustomer Billing() =>
        new("Lucía Gómez", "NIF", "12345678Z", "+34 600 111 222", "lucia@example.es", "Calle Gran Via 28", "ES", null, "Madrid", null);

    private static Customer Incomplete() =>
        Customer.CreateIncompleteFromExternalSale(CustomerId.New(), TenantId, "Lucía", "+34600111222", null, "ES", Now, new PassThroughPhones());

    private static (StoreCustomerDirectory Directory, FakeCustomerRepository Customers, FakeCustomerProvisioning Provisioning) Directory(
        params Customer[] seeded)
    {
        var customers = new FakeCustomerRepository(seeded);
        var provisioning = new FakeCustomerProvisioning();
        return (new StoreCustomerDirectory(customers, provisioning), customers, provisioning);
    }

    [Fact]
    public async Task FindByDocumentFindsAnActiveCustomer()
    {
        var customer = Spaniard();
        var (directory, customers, _) = Directory(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;

        var found = await directory.FindByDocumentAsync(TenantId, "nif", " 12345678Z ", Ct);

        Assert.Equal(customer.Id.Value, found?.Id);
    }

    [Fact]
    public async Task FindByDocumentIgnoresAnInactiveOwner()
    {
        var customer = Spaniard();
        customer.Deactivate(Now);
        var (directory, customers, _) = Directory(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;

        Assert.Null(await directory.FindByDocumentAsync(TenantId, "NIF", "12345678Z", Ct));
    }

    [Fact]
    public async Task CreateReturnsCreatedForANewDocument()
    {
        var created = Spaniard();
        var (directory, _, provisioning) = Directory(created);
        provisioning.NextId = created.Id.Value;

        var result = await directory.CreateAsync(TenantId, null, Billing(), Ct);

        Assert.Equal(StoreCustomerCreationOutcome.Created, result.Outcome);
        Assert.Equal(created.Id.Value, result.Customer?.Id);
    }

    [Fact]
    public async Task CreateReturnsExistingByDocumentWhenAnotherActiveCustomerOwnsIt()
    {
        var owner = Spaniard();
        var (directory, _, provisioning) = Directory(owner);
        provisioning.Created = false;
        provisioning.NextId = owner.Id.Value;

        var result = await directory.CreateAsync(TenantId, null, Billing(), Ct);

        Assert.Equal(StoreCustomerCreationOutcome.ExistingByDocument, result.Outcome);
        Assert.Equal(owner.Id.Value, result.Customer?.Id);
    }

    [Fact]
    public async Task CreateReturnsDocumentOwnerUnavailableWhenTheOwnerIsInactive()
    {
        var owner = Spaniard();
        owner.Deactivate(Now);
        var (directory, _, provisioning) = Directory(owner);
        provisioning.Created = false;
        provisioning.NextId = owner.Id.Value;

        var result = await directory.CreateAsync(TenantId, null, Billing(), Ct);

        Assert.Equal(StoreCustomerCreationOutcome.DocumentOwnerUnavailable, result.Outcome);
        Assert.Null(result.Customer);
    }

    public static TheoryData<string> NotCompletableFailures() =>
    [
        "validation.failed",
        "customers.customer.classification_not_found",
        "customers.customer.city_not_found",
        "customers.customer.document_type_not_allowed",
    ];

    [Theory]
    [MemberData(nameof(NotCompletableFailures))]
    public async Task CreateReturnsNotCompletableForTheDocumentedCodes(string code)
    {
        var (directory, _, provisioning) = Directory();
        provisioning.CreateCompleteThrows = code == "validation.failed"
            ? new FluentValidation.ValidationException("The data does not pass the rules.")
            : new CustomersDomainException(code, "Not completable.");

        var result = await directory.CreateAsync(TenantId, null, Billing(), Ct);

        Assert.Equal(StoreCustomerCreationOutcome.NotCompletable, result.Outcome);
        Assert.Null(result.Customer);
    }

    [Fact]
    public async Task CreatePropagatesOtherErrors()
    {
        var (directory, _, provisioning) = Directory();
        provisioning.CreateCompleteThrows = new CustomersDomainException("customers.customer.identification_type_invalid", "Unknown type.");

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => directory.CreateAsync(TenantId, null, Billing(), Ct));

        Assert.Equal("customers.customer.identification_type_invalid", failure.Code);
    }

    [Fact]
    public async Task CompleteReturnsTheCustomerUnchangedWhenCustomersCannotCompleteIt()
    {
        var customer = Incomplete();
        var (directory, _, provisioning) = Directory(customer);
        provisioning.CompleteFromStoreThrows = new CustomersDomainException("customers.customer.city_not_found", "The city was not found.");

        var result = await directory.CompleteAsync(TenantId, null, customer.Id.Value, Billing(), Ct);

        Assert.Equal(customer.Id.Value, result.Id);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task EnsureAddressesSkipsAllForAnIncompleteCustomer()
    {
        var customer = Incomplete();
        var (directory, _, provisioning) = Directory(customer);
        provisioning.EnsureAddressesThrows = new CustomersDomainException("customers.customer.incomplete", "The customer is incomplete.");
        StoreAddressInput address = new("Lucía", "Calle Gran Via 28", null, "ES", null, "Madrid");

        var outcomes = await directory.EnsureAddressesAsync(TenantId, customer.Id.Value, [address, address], Ct);

        Assert.Equal([StoreAddressOutcome.Skipped, StoreAddressOutcome.Skipped], outcomes);
    }
}
