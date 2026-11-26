using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Provincia guardada (spec 2026-11-23, scope addition): the customer view embedded in a quotation carries the region of the
/// domicile and of each address, resolved in one geography call per lookup.
/// </summary>
public sealed class QuotationCustomerLookupRegionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000002101");
    private static readonly Guid MadridId = Guid.Parse("01900000-0000-7000-8000-000000002102");
    private static readonly Guid BarcelonaId = Guid.Parse("01900000-0000-7000-8000-000000002103");

    [Fact]
    public async Task ForeignCustomerAndAddressCarryRegion()
    {
        var customer = NewForeignCustomer();
        customer.AddAddress(
            new CustomerAddressDetails
            {
                Name = "Sede Barcelona",
                Address = "Calle 2",
                Country = "ES",
                CityName = "Barcelona",
                RegionId = BarcelonaId
            },
            false,
            Now);
        var geography = new RegionGeography();
        var lookup = NewLookup(customer, geography);

        var found = await lookup.FindAsync(TenantId, customer.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(new QuotationRegionDto(MadridId, "Madrid"), found.Region);
        Assert.Equal(new QuotationRegionDto(BarcelonaId, "Barcelona"), Assert.Single(found.Addresses!).Region);
        Assert.Equal(1, geography.FindRegionsCalls);
    }

    [Fact]
    public async Task FindManyResolvesTheRegionsOfTheWholeBatchInOneCall()
    {
        var first = NewForeignCustomer();
        var second = NewForeignCustomer();
        var geography = new RegionGeography();
        var lookup = new QuotationCustomerLookup(
            new FakeCustomerRepository(first, second), geography, null!, companies: null!);

        var found = await lookup.FindManyAsync(
            TenantId, [first.Id.Value, second.Id.Value], TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.All(found.Values, customer => Assert.Equal("Madrid", customer.Region!.Name));
        Assert.Equal(1, geography.FindRegionsCalls);
    }

    [Fact]
    public async Task ACustomerWithoutRegionNeverAsksGeographyForRegions()
    {
        var customer = NewForeignCustomer(withRegion: false);
        var geography = new RegionGeography();
        var lookup = NewLookup(customer, geography);

        var found = await lookup.FindAsync(TenantId, customer.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Null(found.Region);
        Assert.Equal(0, geography.FindRegionsCalls);
    }

    private static QuotationCustomerLookup NewLookup(Customer customer, RegionGeography geography) =>
        new(new FakeCustomerRepository(customer), geography, null!, companies: null!);

    private static Customer NewForeignCustomer(bool withRegion = true) =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI00000001", "Verde Esencial", null, null, null, null,
            new CustomerContactInfo
            {
                Phone = "+34910000000",
                Email = "v@verde.es",
                Address = "Calle 1",
                Country = "ES",
                CityName = "Madrid",
                RegionId = withRegion ? MadridId : null
            },
            new CustomerCommercialInfo { ClassificationId = new ClientClassificationId(Guid.Parse("01900000-0000-7000-8000-000000002120")) },
            Now,
            new PassThroughPhones());

    private sealed class RegionGeography : ICustomerGeographyLookup
    {
        private static readonly Dictionary<Guid, CustomerRegionRef> Regions = new()
        {
            [MadridId] = new(MadridId, "ES", "Madrid"),
            [BarcelonaId] = new(BarcelonaId, "ES", "Barcelona"),
        };

        public int FindRegionsCalls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, CustomerRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken)
        {
            FindRegionsCalls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, CustomerRegionRef>>(
                regionIds.Where(Regions.ContainsKey).Distinct().ToDictionary(id => id, id => Regions[id]));
        }

        public Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            Task.FromResult<CustomerCityRef?>(null);

        public Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerCityRef>>(new Dictionary<Guid, CustomerCityRef>());

        public Task<CustomerCityRef?> FindCityByNameAsync(
            string departmentName, string cityName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentWithCitiesDto>> ListDepartmentsWithCitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> ListCityIdsByDepartmentsAsync(
            IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
