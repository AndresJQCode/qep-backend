using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Customers.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El adaptador que arma la foto de una tasa de retención para un documento (spec impuestos por tipo §7.3, §7.5): la
/// UVT es la del año del documento o, si falta, la del mayor año cargado anterior; sin ninguna anterior queda null.
/// La UVT se congela sólo en tasas con base mínima.
/// </summary>
public sealed class QuotationWithholdingLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000001301");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000001310");

    private static readonly TaxRate Retefuente = TaxRate.Create(
        TaxRateId.New(), TenantId, "Retefuente 2,5 %", 2.5m, "Retefuente", "CO", null, Now);

    private static readonly TaxRate ReteIca = TaxRate.Create(
        TaxRateId.New(), TenantId, "ReteICA Bogotá", 0.966m, "ReteICA", "CO", 4m, Now);

    [Theory]
    [InlineData(2026, 49_799)]
    [InlineData(2027, 49_799)]
    [InlineData(2025, 47_065)]
    public async Task UsesTheUvtOfTheDocumentYearOrTheLatestEarlier(int documentYear, int expectedUvt)
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(documentYear, 6, 1), Uvt(2025, 47_065m), Uvt(2026, 49_799m));

        var reteIca = Assert.Single(rates, rate => rate.TaxRateId == ReteIca.Id.Value);
        Assert.Equal((4m, "UVT", (decimal?)expectedUvt), (reteIca.MinimumBase, reteIca.MinimumBaseUnit, reteIca.TaxUnitValue));
        // Sin base mínima no hay unidad que congelar.
        var retefuente = Assert.Single(rates, rate => rate.TaxRateId == Retefuente.Id.Value);
        Assert.Equal((null, null, null), (retefuente.MinimumBase, retefuente.MinimumBaseUnit, retefuente.TaxUnitValue));
    }

    [Fact]
    public async Task OnlyLaterUvtsLeaveTheUnitValueNull()
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(2024, 6, 1), Uvt(2025, 47_065m));

        Assert.Null(Assert.Single(rates, rate => rate.TaxRateId == ReteIca.Id.Value).TaxUnitValue);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("CO", 2)]
    [InlineData(" co ", 2)]
    [InlineData("ES", 0)]
    public async Task FiltersByCountryWhenGiven(string? country, int expected)
    {
        var rates = await ForCustomerAsync(country, new DateOnly(2026, 6, 1), Uvt(2026, 49_799m));

        Assert.Equal(expected, rates.Count);
    }

    [Fact]
    public async Task CustomerRatesComeInTheCustomerOrderWithTheirPhoto()
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(2026, 6, 1), Uvt(2026, 49_799m));

        Assert.Equal(
            [(Retefuente.Id.Value, "Retefuente 2,5 %", "Retefuente", "CO", 2.5m), (ReteIca.Id.Value, "ReteICA Bogotá", "ReteICA", "CO", 0.966m)],
            rates.Select(rate => (rate.TaxRateId, rate.Name, rate.Type, rate.Country, rate.Percentage)).ToArray());
    }

    [Fact]
    public async Task ForRatesReturnsEveryFoundRateWithItsState()
    {
        var inactive = TaxRate.Create(TaxRateId.New(), TenantId, "Retefuente vieja", 3.5m, "Retefuente", "CO", null, Now);
        inactive.Deactivate(Now);
        var vat = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19 %", 19m, "IVA", "CO", null, Now);
        var lookup = Lookup(new FakeCustomerRepository(), [ReteIca, inactive, vat], Uvt(2026, 49_799m));

        var candidates = await lookup.ForRatesAsync(
            TenantId, [ReteIca.Id.Value, inactive.Id.Value, vat.Id.Value, Guid.NewGuid()], new DateOnly(2026, 6, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, candidates.Count);
        Assert.Equal((true, true, (decimal?)49_799m), (candidates[ReteIca.Id.Value].IsActive, candidates[ReteIca.Id.Value].Withholds, candidates[ReteIca.Id.Value].Rate.TaxUnitValue));
        Assert.False(candidates[inactive.Id.Value].IsActive);
        Assert.False(candidates[vat.Id.Value].Withholds);
    }

    [Fact]
    public async Task AnUnknownCustomerHasNoRates()
    {
        var lookup = Lookup(new FakeCustomerRepository(), [Retefuente]);

        var rates = await lookup.ForCustomerAsync(
            TenantId, Guid.NewGuid(), null, new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);

        Assert.Empty(rates);
    }

    private static async Task<IReadOnlyList<Modules.Quotations.Domain.WithholdingRate>> ForCustomerAsync(
        string? country, DateOnly documentDate, params TaxUnitValue[] uvts)
    {
        var customer = NewCustomer();
        customer.SetWithholdingTaxRates([Retefuente.Id.Value, ReteIca.Id.Value], Now);
        var lookup = Lookup(new FakeCustomerRepository(customer), [Retefuente, ReteIca], uvts);

        return await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, country, documentDate, TestContext.Current.CancellationToken);
    }

    private static QuotationWithholdingLookup Lookup(
        FakeCustomerRepository customers, TaxRate[] rates, params TaxUnitValue[] uvts) =>
        new(customers, new FakeTaxRateRepository(rates), new InMemoryTaxUnitValues(uvts));

    private static TaxUnitValue Uvt(int year, decimal value) =>
        TaxUnitValue.Create("CO", year, value, Guid.NewGuid(), Now, currentYear: 2027);

    private static Customer NewCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000143",
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
                ClassificationId = new ClientClassificationId(Guid.Parse("01900000-0000-7000-8000-000000001320")),
                VatSurplus = false
            },
            Now);

    // Compara crudo, como el repositorio real (TaxUnitValueRepository): quien llama pasa el código canónico.
    private sealed class InMemoryTaxUnitValues(params TaxUnitValue[] values) : ITaxUnitValueRepository
    {
        public Task<IReadOnlyList<TaxUnitValue>> ListAsync(string? country, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaxUnitValue>>(
                [.. values.Where(value => country is null || value.Country == country).OrderByDescending(value => value.Year)]);

        public Task<TaxUnitValue?> FindAsync(string country, int year, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyAsync(string country, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(TaxUnitValue value) => throw new NotSupportedException();
    }
}
