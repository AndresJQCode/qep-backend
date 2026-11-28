using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>El alta de un cliente con sus perfiles de facturación (spec 2026-11-27): la validación con clave indexada, las
/// referencias malas que no queman un número del CUC y el orden en que nacen los perfiles.</summary>
public sealed class CreateCustomerBillingProfilesTests
{
    private static readonly EngagementTestBed.Context Manager =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private static NewCustomerBillingProfile Profile(
        string name = "Comercial Andina SAS",
        string number = "900123456",
        bool isPrincipal = false,
        Guid? cityId = null,
        Guid? regionId = null,
        IReadOnlyList<Guid>? taxRates = null) =>
        new(name, "Juridica", "NIT", number, "3001234567", "facturas@andina.co", "Cra 43A # 1-50", "CO",
            cityId ?? CityId, null, regionId, taxRates, false, isPrincipal);

    private static CreateCustomerCommand Command(params NewCustomerBillingProfile[] profiles) =>
        new(CustomerWriteDoubles.Tenant, "Laura Pérez", null, "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO", CityId,
            null, Classification.Id.Value, false, BillingProfiles: profiles);

    private static CreateCustomerHandler NewCreate(FakeCustomerRepository customers, List<string> calls) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new TaxRates(), new Geography([]),
            new FakeCustomerCompanyDirectory([]), new UnitOfWork(), new RecordingAudit(), new Cucs(calls), Manager,
            new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer(), new TenantDirectory());

    [Fact]
    public void AnInvalidProfileIsReportedWithItsIndexedKey()
    {
        var result = new CreateCustomerValidator().Validate(Command(Profile(), Profile(name: "")));

        Assert.Contains(result.Errors, error => error.PropertyName == "BillingProfiles[1].Name");
        Assert.DoesNotContain(result.Errors, error => error.PropertyName.StartsWith("BillingProfiles[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPrincipalProfilesAreRejectedOnTheList()
    {
        var result = new CreateCustomerValidator().Validate(
            Command(Profile(isPrincipal: true), Profile(number: "900123457", isPrincipal: true)));

        var error = Assert.Single(result.Errors);
        Assert.Equal("BillingProfiles", error.PropertyName);
        Assert.Equal("Sólo un perfil de facturación puede ser el principal.", error.ErrorMessage);
    }

    [Fact]
    public void NoProfilesAtAllIsValid() =>
        Assert.True(new CreateCustomerValidator().Validate(Command()).IsValid);

    [Fact]
    public async Task MoreThanTheLimitFailsWithTheLimitCodeBeforeTheCuc()
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository();
        var profiles = Enumerable.Range(0, CustomerBillingProfile.MaxPerCustomer + 1)
            .Select(i => Profile(number: (900000000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, calls).HandleAsync(Command(profiles), TestContext.Current.CancellationToken));

        Assert.Equal(CustomerBillingProfileErrors.LimitReached, failure.Code);
        Assert.DoesNotContain("cuc", calls);
        Assert.Empty(customers.Added);
    }

    [Fact]
    public async Task ABadRegionInTheSecondProfileCreatesNothingAndSkipsTheCuc()
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, calls).HandleAsync(
                Command(Profile(), Profile(number: "900123457", regionId: Guid.NewGuid())),
                TestContext.Current.CancellationToken));

        Assert.Equal("customers.billing_profile.region_invalid", failure.Code);
        Assert.DoesNotContain("cuc", calls);
        Assert.Empty(customers.Added);
    }

    [Fact]
    public async Task AnUnknownCityInTheSecondProfileCreatesNothingAndSkipsTheCuc()
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, calls).HandleAsync(
                Command(Profile(), Profile(number: "900123457", cityId: Guid.NewGuid())),
                TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.city_not_found", failure.Code);
        Assert.DoesNotContain("cuc", calls);
        Assert.Empty(customers.Added);
    }

    [Fact]
    public async Task AnUnknownTaxRateInTheSecondProfileCreatesNothingAndSkipsTheCuc()
    {
        var calls = new List<string>();
        var customers = new FakeCustomerRepository();

        await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, calls).HandleAsync(
                Command(Profile(), Profile(number: "900123457", taxRates: [Guid.NewGuid()])),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("cuc", calls);
        Assert.Empty(customers.Added);
    }

    [Fact]
    public async Task TheFirstProfileIsPrincipalWhenNoneAsksAndTheyKeepTheirOrder()
    {
        var customers = new FakeCustomerRepository();

        await NewCreate(customers, []).HandleAsync(
            Command(Profile(name: "Primero"), Profile(name: "Segundo", number: "900123457")),
            TestContext.Current.CancellationToken);

        var profiles = Assert.Single(customers.Added).BillingProfiles.ToArray();
        Assert.Equal(["Primero", "Segundo"], profiles.Select(p => p.Name));
        Assert.Equal([true, false], profiles.Select(p => p.IsPrincipal));
    }

    [Fact]
    public async Task ThePrincipalThatTheBodyAsksForWins()
    {
        var customers = new FakeCustomerRepository();

        await NewCreate(customers, []).HandleAsync(
            Command(Profile(name: "Primero"), Profile(name: "Segundo", number: "900123457", isPrincipal: true)),
            TestContext.Current.CancellationToken);

        var profiles = Assert.Single(customers.Added).BillingProfiles.ToArray();
        Assert.Equal(["Segundo"], profiles.Where(p => p.IsPrincipal).Select(p => p.Name));
    }
}
