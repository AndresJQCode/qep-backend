using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>QuotationCustomerRef.WithRetention</c> ya no es una columna del cliente: el adaptador lo deriva de sus tasas
/// asignadas (spec impuestos por tipo §9, <c>client.withRetention</c> para el frontend viejo). Cuenta sólo una tasa de
/// tipo Retefuente, de cualquier país, que siga existiendo en el catálogo.
/// </summary>
public sealed class QuotationCustomerWithRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000001101");

    private static readonly CustomerTaxRateRef RetefuenteSpain =
        new(Guid.Parse("01900000-0000-7000-8000-000000001102"), "Retención IRPF 15 %", "Retefuente", "ES", 15m, true, true);

    private static readonly CustomerTaxRateRef ReteIca =
        new(Guid.Parse("01900000-0000-7000-8000-000000001103"), "ReteICA 0,966 %", "ReteICA", "CO", 0.966m, true, true);

    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000001110");

    private static readonly Guid DeletedRetefuenteId = Guid.Parse("01900000-0000-7000-8000-000000001104");

    [Fact]
    public async Task ACustomerWithARetefuenteOfAnyCountryWithholds()
    {
        Assert.True(await WithRetentionAsync(RetefuenteSpain.Id, ReteIca.Id));
    }

    [Fact]
    public async Task AReteIcaOnlyCustomerDoesNotWithhold()
    {
        Assert.False(await WithRetentionAsync(ReteIca.Id));
    }

    // La tasa se borró del catálogo después de asignarla: ya no hay de dónde saber su tipo, y no cuenta.
    [Fact]
    public async Task ARateDeletedFromTheCatalogDoesNotCount()
    {
        Assert.False(await WithRetentionAsync(DeletedRetefuenteId));
    }

    [Fact]
    public async Task ACustomerWithoutRatesDoesNotWithhold()
    {
        Assert.False(await WithRetentionAsync());
    }

    private static async Task<bool> WithRetentionAsync(params Guid[] taxRateIds)
    {
        var customer = NewCustomer();
        if (taxRateIds.Length > 0)
        {
            customer.SetWithholdingTaxRates(taxRateIds, Now);
        }

        var lookup = new QuotationCustomerLookup(
            new FakeCustomerRepository(customer),
            new EmptyGeography(),
            new CatalogRates(RetefuenteSpain, ReteIca));

        var found = await lookup.FindAsync(TenantId, customer.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        return found.WithRetention;
    }

    private static Customer NewCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial S.A.S.",
            null,
            new CustomerAddressDetails { Name = "Principal", Address = "Calle 10 # 45-12", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900.123.456-1" },
            null,
            new CustomerContactInfo
            {
                Phone = "310 935 2187",
                Email = "compras@verde.co",
                Address = "Calle 10 # 45-12",
                Country = Customer.ColombiaCountryCode,
                CityId = CityId
            },
            new CustomerCommercialInfo
            {
                ClassificationId = new ClientClassificationId(Guid.Parse("01900000-0000-7000-8000-000000001120")),
                VatSurplus = false
            },
            Now);

    /// <summary>El catálogo de tasas del tenant: lo que no está acá se comporta como una tasa borrada.</summary>
    private sealed class CatalogRates(params CustomerTaxRateRef[] rates) : ICustomerTaxRateLookup
    {
        public Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerTaxRateRef>>(
                rates.Where(rate => taxRateIds.Contains(rate.Id)).ToDictionary(rate => rate.Id));

        public Task<IReadOnlyList<CustomerTaxRateRef>> ListWithholdingAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyGeography : ICustomerGeographyLookup
    {
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
