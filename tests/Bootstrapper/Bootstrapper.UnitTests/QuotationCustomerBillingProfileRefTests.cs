using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers -> Quotations, libreta de perfiles de facturación (spec 2026-11-24 §3.6): el bloque <c>client</c> de la
/// cotización y del pedido los recibe resueltos —ciudad, departamento, región y tasas—, la principal primero.
/// </summary>
public sealed class QuotationCustomerBillingProfileRefTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000001201");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000001210");
    private static readonly Guid DepartmentId = Guid.Parse("01900000-0000-7000-8000-000000001211");
    private static readonly Guid MadridId = Guid.Parse("01900000-0000-7000-8000-000000001212");
    private static readonly Guid DeletedRateId = Guid.Parse("01900000-0000-7000-8000-000000001213");

    private static readonly CustomerTaxRateRef Retefuente =
        new(Guid.Parse("01900000-0000-7000-8000-000000001214"), "Retefuente 2,5 %", "Retefuente", "CO", 2.5m, true, true);

    [Fact]
    public async Task ToRefCarriesTheBillingProfilesPrincipalFirstWithResolvedCities()
    {
        var customer = NewCustomer();
        customer.EnsureBillingProfile(
            new CustomerBillingProfileDetails
            {
                Name = "Zeta SAS",
                PersonType = "Juridica",
                IdentificationType = "NIT",
                IdentificationNumber = "900123456",
                Phone = "3001234567",
                Email = "facturas@zeta.co",
                Address = "Cra 43A # 1-50",
                CityId = CityId,
                WithholdingTaxRateIds = [Retefuente.Id, DeletedRateId],
                VatSurplus = true
            },
            false,
            Now);
        customer.EnsureBillingProfile(
            new CustomerBillingProfileDetails
            {
                Name = "Andina Iberia SL",
                IdentificationType = "NIF",
                IdentificationNumber = "B12345678",
                Address = "Calle Mayor 1",
                Country = "ES",
                CityName = "Madrid",
                RegionId = MadridId
            },
            false,
            Now);
        var lookup = new QuotationCustomerLookup(
            new FakeCustomerRepository(customer), new Geography(), new CatalogRates(Retefuente), companies: null!);

        var found = await lookup.FindAsync(TenantId, customer.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(CustomerBillingProfile.MaxPerCustomer, found.BillingProfileLimit);
        Assert.NotNull(found.BillingProfiles);
        Assert.Equal(["Zeta SAS", "Andina Iberia SL"], found.BillingProfiles.Select(profile => profile.Name));
        var colombian = found.BillingProfiles[0];
        Assert.True(colombian.IsPrincipal);
        Assert.Equal("Juridica", colombian.PersonType);
        Assert.Equal("NIT", colombian.IdentificationType);
        Assert.Equal("900123456", colombian.IdentificationNumber);
        Assert.Equal("3001234567", colombian.Phone);
        Assert.Equal("facturas@zeta.co", colombian.Email);
        Assert.Equal("CO", colombian.Country);
        Assert.Equal(CityId, colombian.CityId);
        Assert.Equal("Medellín", colombian.CityName);
        Assert.Equal(DepartmentId, colombian.DepartmentId);
        Assert.Equal("Antioquia", colombian.DepartmentName);
        Assert.Null(colombian.Region);
        Assert.True(colombian.VatSurplus);
        // La tasa borrada del catálogo se omite.
        Assert.Equal(
            new QuotationClientWithholdingRateResponse(
                Retefuente.Id, "Retefuente 2,5 %", "Retefuente", "CO", 2.5m, true, "All"),
            Assert.Single(colombian.WithholdingTaxRates));
        var spanish = found.BillingProfiles[1];
        Assert.False(spanish.IsPrincipal);
        Assert.Equal("Madrid", spanish.CityName);
        Assert.Null(spanish.CityId);
        Assert.Null(spanish.DepartmentId);
        Assert.Null(spanish.DepartmentName);
        Assert.Equal(new QuotationRegionDto(MadridId, "Madrid"), spanish.Region);
        Assert.Empty(spanish.WithholdingTaxRates);
        // Las tasas de un perfil no hacen retenedor al cliente.
        Assert.False(found.WithRetention);
    }

    private static Customer NewCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial S.A.S.",
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900.123.456-1" },
            null,
            new CustomerContactInfo { Phone = "310 935 2187", Country = Customer.ColombiaCountryCode, CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = null },
            Now);

    private sealed class CatalogRates(params CustomerTaxRateRef[] rates) : ICustomerTaxRateLookup
    {
        public Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerTaxRateRef>>(
                rates.Where(rate => taxRateIds.Contains(rate.Id)).ToDictionary(rate => rate.Id));

        public Task<IReadOnlyList<CustomerTaxRateRef>> ListWithholdingAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Geography : ICustomerGeographyLookup
    {
        public Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerCityRef>>(
                cityIds.Where(id => id == CityId).Distinct().ToDictionary(
                    id => id, id => new CustomerCityRef(id, "05001", "Medellín", DepartmentId, "05", "Antioquia")));

        public Task<IReadOnlyDictionary<Guid, CustomerRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerRegionRef>>(
                regionIds.Where(id => id == MadridId).Distinct().ToDictionary(
                    id => id, id => new CustomerRegionRef(id, "ES", "Madrid")));

        public Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
