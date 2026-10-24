using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec de Shopify 2026-10-15 §6.2.1 pasos 3 y 4 sin base: el alta de un comprador desde una integración
/// repite el orden de <c>CreateCustomerHandler</c> (clasificación y ciudad antes del CUC), gana la carrera del documento
/// releyendo y audita con actor <c>Integration</c>.</summary>
public sealed class CustomerProvisioningTests
{
    private sealed record Bed(
        CustomerProvisioning Provisioning, FakeCustomerRepository Customers, RecordingAudit Audit, List<string> Calls);

    private static Bed NewBed(Customer? existing = null, Exception? saveFailure = null)
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository { Existing = existing };
        var audit = new RecordingAudit();
        var provisioning = new CustomerProvisioning(
            customers,
            new Classifications(calls),
            new Geography(calls),
            new UnitOfWork(saveFailure),
            audit,
            new Cucs(calls),
            new FixedClock(),
            new CreateCustomerValidator(),
            new PlusNormalizer());
        return new Bed(provisioning, customers, audit, calls);
    }

    private static CompleteCustomerData Buyer(string? email = "Laura@Verde.co") =>
        new("Laura Pérez", "CC", "1020304050", "310 935 2187", email, "Calle 10 # 45-12", "CO", CityId, null, Classification.Id.Value);

    [Fact]
    public async Task CreateCompleteIssuesCucAfterClassificationAndCity()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(["classification", "city", "cuc"], bed.Calls);
        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, true, true, true), result);
        Assert.Equal("CLI08000007", customer.Cuc);
        Assert.Equal(Tenant, customer.TenantId);
        Assert.False(customer.WithRetention);
        Assert.False(customer.VatSurplus);
        Assert.Equal("+573109352187", customer.PhoneE164);
        // La libreta nace con la dirección del alta, como en el POST.
        Assert.Equal(CityId, Assert.Single(customer.Addresses).CityId);
    }

    [Fact]
    public async Task CreateCompleteInvalidDataFailsBeforeIssuingCuc()
    {
        var bed = NewBed();

        await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(email: "no-es-un-correo"), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Calls);
        Assert.Empty(bed.Customers.Added);
    }

    [Fact]
    public async Task CreateCompleteIdentificationTakenRaceReturnsExisting()
    {
        var existing = Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", "Laura", null, null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            new CustomerContactInfo { Phone = "3000000000", Email = "otra@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now);
        var bed = NewBed(existing, new CustomersDomainException("customers.customer.identification_taken", "taken"));

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(new ProvisionedCustomer(existing.Id.Value, true, true, false), result);
    }

    [Fact]
    public async Task CreateCompleteOtherDomainFailureIsNotSwallowed()
    {
        var bed = NewBed(saveFailure: new CustomersDomainException("customers.customer.cuc_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.cuc_taken", failure.Code);
    }

    [Fact]
    public async Task CreateCompleteAuditsWithIntegrationActor()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Empty(bed.Audit.Human);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteWithoutNameUsesShopifyDefault()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateIncompleteAsync(
            Tenant, "  ", "310 935 2187", "laura@verde.co", "CO", TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, false, true, true), result);
        Assert.Equal(CustomerProvisioning.ExternalSaleFallbackName, customer.Name);
        Assert.Equal("Cliente de Shopify", CustomerProvisioning.ExternalSaleFallbackName);
        Assert.Equal("+573109352187", customer.PhoneE164);
        Assert.Empty(bed.Calls);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteTrimsLongNameToTheColumn()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateIncompleteAsync(
            Tenant, new string('a', Customer.NameMaxLength + 10), null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Customer.NameMaxLength, Assert.Single(bed.Customers.Added).Name.Length);
    }

    [Fact]
    public async Task CreateFromRegisterAddsAnIncompleteAndAuditsTheCashierAsActor()
    {
        var bed = NewBed();
        var cashierId = Guid.NewGuid();

        var result = await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, cashierId, new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.False(customer.IsComplete);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, false, true, true), result);
        Assert.Empty(bed.Audit.ByIntegration);
        Assert.Equal(
            (Tenant, cashierId, "customers.customer.created", customer.Id.ToString(), "success", (string?)null),
            Assert.Single(bed.Audit.Human));
    }

    [Fact]
    public async Task CreateFromRegisterLetsTheIdentificationRaceThrough()
    {
        var bed = NewBed(saveFailure: new CustomersDomainException("customers.customer.identification_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_taken", failure.Code);
    }

    [Fact]
    public async Task CreateFromRegisterRejectsAnUnknownDocumentType()
    {
        var bed = NewBed();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "RUT", "1020304050", null), source: null, TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.identification_type_invalid", failure.Code);
        Assert.Empty(bed.Customers.Added);
    }

    [Fact]
    public async Task CreateFromRegisterAuditsWithTheGivenSource()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), "pos", TestContext.Current.CancellationToken);

        Assert.Equal("pos", Assert.Single(bed.Audit.Human).Source);
    }

    [Fact]
    public async Task CreateFromRegisterWithoutSourceAuditsWithTheDefault()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateFromRegisterAsync(
            Tenant, Guid.NewGuid(), new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), source: null, TestContext.Current.CancellationToken);

        // El publisher real resuelve null a «customers».
        Assert.Null(Assert.Single(bed.Audit.Human).Source);
    }

    [Fact]
    public async Task OtherProvisioningPathsKeepAuditingWithoutSource()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);
        await bed.Provisioning.CreateIncompleteAsync(Tenant, "Laura", null, null, null, TestContext.Current.CancellationToken);

        // Las dos van por PublishByIntegration, que no lleva source: sale «customers» como siempre.
        Assert.Empty(bed.Audit.Human);
        Assert.Equal(2, bed.Audit.ByIntegration.Count);
    }
}
