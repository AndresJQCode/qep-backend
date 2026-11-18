using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Companies.Domain;
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
    private static readonly Guid Bogota = Guid.Parse("01900000-0000-7000-8000-000000001311");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-000000001312");
    private static readonly Guid Cali = Guid.Parse("01900000-0000-7000-8000-000000001313");

    private static readonly TaxRate Retefuente = TaxRate.Create(
        TaxRateId.New(), TenantId, "Retefuente 2,5 %", 2.5m, "Retefuente", "CO", null, null, Now);

    // Nacional y con base mínima: desde el spec ReteICA por empresa (A15) el cliente no puede tener una ReteICA, y la
    // unidad tributaria se congela igual en cualquier tasa con base.
    private static readonly TaxRate WithBase = TaxRate.Create(
        TaxRateId.New(), TenantId, "Retefuente servicios 4 %", 4m, "Retefuente", "CO", 4m, null, Now);

    [Theory]
    [InlineData(2026, 49_799)]
    [InlineData(2027, 49_799)]
    [InlineData(2025, 47_065)]
    public async Task UsesTheUvtOfTheDocumentYearOrTheLatestEarlier(int documentYear, int expectedUvt)
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(documentYear, 6, 1), Uvt(2025, 47_065m), Uvt(2026, 49_799m));

        var withBase = Assert.Single(rates, rate => rate.TaxRateId == WithBase.Id.Value);
        Assert.Equal((4m, "UVT", (decimal?)expectedUvt), (withBase.MinimumBase, withBase.MinimumBaseUnit, withBase.TaxUnitValue));
        // Sin base mínima no hay unidad que congelar.
        var retefuente = Assert.Single(rates, rate => rate.TaxRateId == Retefuente.Id.Value);
        Assert.Equal((null, null, null), (retefuente.MinimumBase, retefuente.MinimumBaseUnit, retefuente.TaxUnitValue));
    }

    [Fact]
    public async Task OnlyLaterUvtsLeaveTheUnitValueNull()
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(2024, 6, 1), Uvt(2025, 47_065m));

        Assert.Null(Assert.Single(rates, rate => rate.TaxRateId == WithBase.Id.Value).TaxUnitValue);
    }

    // Spec retenciones por UVT §5.2: la foto congela el año de la UVT que usó, para que un documento de 2026 con sólo la
    // de 2025 cargada muestre que está desactualizada. Sin base mínima no hay unidad ni año.
    [Fact]
    public async Task PhotoFreezesTheTaxUnitYear()
    {
        var rates = await ForCustomerAsync(country: null, new DateOnly(2026, 1, 10), Uvt(2025, 49_799m));

        var withBase = Assert.Single(rates, rate => rate.TaxRateId == WithBase.Id.Value);
        Assert.Equal(((decimal?)49_799m, (int?)2025), (withBase.TaxUnitValue, withBase.TaxUnitYear));
        Assert.Null(Assert.Single(rates, rate => rate.TaxRateId == Retefuente.Id.Value).TaxUnitYear);
    }

    // Spec retenciones por UVT §5.4: el refresco pide la UVT vigente de cada país con la regla de la foto (el año del
    // documento o el mayor anterior). Un país sin valores no sale en el diccionario.
    [Fact]
    public async Task TaxUnitsAsyncResolvesPerCountry()
    {
        var lookup = Lookup(new FakeCustomerRepository(), [], Uvt(2025, 47_065m), Uvt(2026, 49_799m));

        var units = await lookup.TaxUnitsAsync(["CO", "ES"], new DateOnly(2026, 3, 1), TestContext.Current.CancellationToken);

        Assert.Equal(
            [new KeyValuePair<string, Modules.Quotations.Domain.TaxUnitPhoto>("CO", new(49_799m, 2026))],
            units.ToArray());
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
            [(Retefuente.Id.Value, "Retefuente 2,5 %", "Retefuente", "CO", 2.5m), (WithBase.Id.Value, "Retefuente servicios 4 %", "Retefuente", "CO", 4m)],
            rates.Select(rate => (rate.TaxRateId, rate.Name, rate.Type, rate.Country, rate.Percentage)).ToArray());
    }

    [Fact]
    public async Task ForRatesReturnsEveryFoundRateWithItsState()
    {
        var inactive = TaxRate.Create(TaxRateId.New(), TenantId, "Retefuente vieja", 3.5m, "Retefuente", "CO", null, null, Now);
        inactive.Deactivate(Now);
        var vat = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19 %", 19m, "IVA", "CO", null, null, Now);
        var lookup = Lookup(new FakeCustomerRepository(), [WithBase, inactive, vat], Uvt(2026, 49_799m));

        var candidates = await lookup.ForRatesAsync(
            TenantId, [WithBase.Id.Value, inactive.Id.Value, vat.Id.Value, Guid.NewGuid()], new DateOnly(2026, 6, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, candidates.Count);
        Assert.Equal((true, true, (decimal?)49_799m), (candidates[WithBase.Id.Value].IsActive, candidates[WithBase.Id.Value].Withholds, candidates[WithBase.Id.Value].Rate.TaxUnitValue));
        Assert.False(candidates[inactive.Id.Value].IsActive);
        Assert.False(candidates[vat.Id.Value].Withholds);
    }

    // Una tasa de un país que la tabla de países no conoce (no debería existir, pero es una fila de otro módulo) no
    // hace fallar la foto: el filtro por país la descarta antes de preguntar si retiene, y sin país no retiene.
    [Fact]
    public async Task ARateOfAnUnknownCountryNeverThrows()
    {
        var foreign = TaxRate.Create(TaxRateId.New(), TenantId, "Retención extranjera", 2m, "Retefuente", "CO", null, null, Now);
        typeof(TaxRate).GetProperty(nameof(TaxRate.Country))!.SetValue(foreign, "ZZ");
        var customer = NewCustomer();
        customer.SetWithholdingTaxRates([Retefuente.Id.Value, foreign.Id.Value], Now);
        var lookup = Lookup(new FakeCustomerRepository(customer), [Retefuente, foreign]);

        var forCompany = await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, "CO", new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);
        var anyCountry = await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, null, new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);
        var candidates = await lookup.ForRatesAsync(
            TenantId, [foreign.Id.Value], new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);

        Assert.Equal(Retefuente.Id.Value, Assert.Single(forCompany).TaxRateId);
        Assert.Equal(Retefuente.Id.Value, Assert.Single(anyCountry).TaxRateId);
        Assert.False(candidates[foreign.Id.Value].Withholds);
    }

    [Fact]
    public async Task AnUnknownCustomerHasNoRates()
    {
        var lookup = Lookup(new FakeCustomerRepository(), [Retefuente]);

        var rates = await lookup.ForCustomerAsync(
            TenantId, Guid.NewGuid(), null, new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);

        Assert.Empty(rates);
    }

    // A15: el cliente ya no puede tener municipales; si una quedó asignada, no entra a la foto.
    [Fact]
    public async Task ForCustomerAsyncReturnsOnlyNationalRates()
    {
        var reteIca = TaxRate.Create(TaxRateId.New(), TenantId, "ReteICA Bogotá", 0.966m, "ReteICA", "CO", null, Bogota, Now);
        var customer = NewCustomer();
        customer.SetWithholdingTaxRates([reteIca.Id.Value, Retefuente.Id.Value], Now);
        var lookup = Lookup(new FakeCustomerRepository(customer), [Retefuente, reteIca]);

        var forCompany = await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, "CO", new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);
        var anyCountry = await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, null, new DateOnly(2026, 6, 1), TestContext.Current.CancellationToken);

        Assert.Equal(Retefuente.Id.Value, Assert.Single(forCompany).TaxRateId);
        Assert.Equal(Retefuente.Id.Value, Assert.Single(anyCountry).TaxRateId);
    }

    // Spec ReteICA por empresa §7.5: las tasas municipales de la empresa para ese municipio, activas, ya como foto con su
    // municipio; las de otro municipio o inactivas no salen.
    [Fact]
    public async Task ForCompanyMunicipalAsyncFiltersByCityAndActive()
    {
        var bogota = TaxRate.Create(TaxRateId.New(), TenantId, "ReteICA Bogotá", 0.966m, "ReteICA", "CO", 4m, Bogota, Now);
        var medellin = TaxRate.Create(TaxRateId.New(), TenantId, "ReteICA Medellín", 1m, "ReteICA", "CO", null, Medellin, Now);
        var inactive = TaxRate.Create(TaxRateId.New(), TenantId, "ReteICA Cali", 0.5m, "ReteICA", "CO", null, Cali, Now);
        inactive.Deactivate(Now);
        var notAssigned = TaxRate.Create(TaxRateId.New(), TenantId, "ReteICA Bogotá 2", 0.7m, "ReteICA", "CO", null, Bogota, Now);
        var company = NewCompany();
        company.SetWithholdingTaxRates([bogota.Id.Value, medellin.Id.Value, inactive.Id.Value], Now);
        var lookup = new QuotationWithholdingLookup(
            new FakeCustomerRepository(), new FakeCompanyRepository(company),
            new FakeTaxRateRepository(bogota, medellin, inactive, notAssigned, Retefuente),
            new InMemoryTaxUnitValues(Uvt(2026, 49_799m)));
        var date = new DateOnly(2026, 6, 1);
        var ct = TestContext.Current.CancellationToken;

        var inBogota = await lookup.ForCompanyMunicipalAsync(TenantId, company.Id.Value, "CO", Bogota, date, ct);
        var inCali = await lookup.ForCompanyMunicipalAsync(TenantId, company.Id.Value, "CO", Cali, date, ct);
        var otherCountry = await lookup.ForCompanyMunicipalAsync(TenantId, company.Id.Value, "ES", Bogota, date, ct);
        var unknownCompany = await lookup.ForCompanyMunicipalAsync(TenantId, Guid.NewGuid(), "CO", Bogota, date, ct);

        var photo = Assert.Single(inBogota);
        Assert.Equal(
            (bogota.Id.Value, "ReteICA", 0.966m, (Guid?)Bogota, (decimal?)49_799m),
            (photo.TaxRateId, photo.Type, photo.Percentage, photo.CityId, photo.TaxUnitValue));
        Assert.Equal(0.966m, photo.CatalogPercentage);
        Assert.Empty(inCali);
        Assert.Empty(otherCountry);
        Assert.Empty(unknownCompany);
    }

    // Spec 2026-11-18 §4.2: la foto lleva el alcance de la tasa, que es lo que decide su base en el documento. Una tasa
    // sin alcance sale All.
    [Fact]
    public async Task ThePhotoCarriesTheRateScope()
    {
        var services = TaxRate.Create(
            TaxRateId.New(), TenantId, "Retefuente servicios 4 %", 4m, "Retefuente", "CO", null, null, Now,
            BuildingBlocks.Domain.Taxes.WithholdingScope.Services);
        var lookup = Lookup(new FakeCustomerRepository(), [services, Retefuente]);

        var candidates = await lookup.ForRatesAsync(
            TenantId, [services.Id.Value, Retefuente.Id.Value], new DateOnly(2026, 6, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(BuildingBlocks.Domain.Taxes.WithholdingScope.Services, candidates[services.Id.Value].Rate.AppliesTo);
        Assert.Equal(BuildingBlocks.Domain.Taxes.WithholdingScope.All, candidates[Retefuente.Id.Value].Rate.AppliesTo);
    }

    [Fact]
    public async Task IsWithholdingAgentReadsTheCustomersList()
    {
        var customer = NewCustomer();
        customer.SetWithholdingAgentCities([new WithholdingAgentCityEntry("CO", "ReteICA", Bogota)], Now);
        var lookup = Lookup(new FakeCustomerRepository(customer), [Retefuente]);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await lookup.IsWithholdingAgentAsync(TenantId, customer.Id.Value, "co", "ReteICA", Bogota, ct));
        Assert.False(await lookup.IsWithholdingAgentAsync(TenantId, customer.Id.Value, "CO", "ReteICA", Medellin, ct));
        Assert.False(await lookup.IsWithholdingAgentAsync(TenantId, Guid.NewGuid(), "CO", "ReteICA", Bogota, ct));
    }

    // El composer pregunta por el mismo cliente dos o tres veces en un request (si es agente y sus tasas): una lectura.
    [Fact]
    public async Task TheCustomerIsReadOnceAcrossAgentCheckAndCustomerRates()
    {
        var customer = NewCustomer();
        var customers = new FakeCustomerRepository(customer);
        var lookup = Lookup(customers, [Retefuente]);
        var ct = TestContext.Current.CancellationToken;

        await lookup.IsWithholdingAgentAsync(TenantId, customer.Id.Value, "CO", "ReteICA", CityId, ct);
        await lookup.ForCustomerAsync(TenantId, customer.Id.Value, null, new DateOnly(2026, 6, 1), ct);

        Assert.Equal(1, customers.FindCalls);
    }

    // Review Focus 5: el memo guarda la instancia que siguió el repositorio, no una copia. Si el mismo scope le cambia
    // los municipios al cliente, la siguiente pregunta lo ve sin volver a la base.
    [Fact]
    public async Task TheMemoServesTheTrackedCustomerWithoutReReading()
    {
        var customer = NewCustomer();
        var customers = new FakeCustomerRepository(customer);
        var lookup = Lookup(customers, [Retefuente]);
        var ct = TestContext.Current.CancellationToken;

        var before = await lookup.IsWithholdingAgentAsync(TenantId, customer.Id.Value, "CO", "ReteICA", Bogota, ct);
        customer.SetWithholdingAgentCities([new WithholdingAgentCityEntry("CO", "ReteICA", Bogota)], Now);
        var after = await lookup.IsWithholdingAgentAsync(TenantId, customer.Id.Value, "CO", "ReteICA", Bogota, ct);

        Assert.False(before);
        Assert.True(after);
        Assert.Equal(1, customers.FindCalls);
    }

    [Fact]
    public async Task TheCompanyIsReadOnceAcrossMunicipalLookups()
    {
        var company = NewCompany();
        var companies = new FakeCompanyRepository(company);
        var lookup = new QuotationWithholdingLookup(
            new FakeCustomerRepository(), companies, new FakeTaxRateRepository(Retefuente), new InMemoryTaxUnitValues());
        var date = new DateOnly(2026, 6, 1);
        var ct = TestContext.Current.CancellationToken;

        await lookup.ForCompanyMunicipalAsync(TenantId, company.Id.Value, "CO", Bogota, date, ct);
        await lookup.ForCompanyMunicipalAsync(TenantId, company.Id.Value, "CO", Medellin, date, ct);

        Assert.Equal(1, companies.FindCalls);
    }

    private static Company NewCompany() =>
        Company.Create(
            CompanyId.New(), TenantId, "Verde S.A.S.",
            [new CompanyBankAccount { BankName = "Bancolombia", AccountNumber = "CTA-1", Currency = "COP" }],
            "900.111.222-3", Company.ColombiaCountryCode, CompanyDocumentType.Nit, Bogota, null,
            CompanyContactInfo.Empty, Now);

    private static async Task<IReadOnlyList<Modules.Quotations.Domain.WithholdingRate>> ForCustomerAsync(
        string? country, DateOnly documentDate, params TaxUnitValue[] uvts)
    {
        var customer = NewCustomer();
        customer.SetWithholdingTaxRates([Retefuente.Id.Value, WithBase.Id.Value], Now);
        var lookup = Lookup(new FakeCustomerRepository(customer), [Retefuente, WithBase], uvts);

        return await lookup.ForCustomerAsync(
            TenantId, customer.Id.Value, country, documentDate, TestContext.Current.CancellationToken);
    }

    private static QuotationWithholdingLookup Lookup(
        FakeCustomerRepository customers, TaxRate[] rates, params TaxUnitValue[] uvts) =>
        new(customers, new FakeCompanyRepository(), new FakeTaxRateRepository(rates), new InMemoryTaxUnitValues(uvts));

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
