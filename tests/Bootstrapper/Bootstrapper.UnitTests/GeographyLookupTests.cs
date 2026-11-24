using Modules.Catalog.Application;
using Modules.Geography.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo resuelven los lookups de Geography las ciudades de una página. Un cliente o una empresa sin
/// ciudad es común —el pedido de un cliente sin ciudad pide la lista vacía—, y esa lista vacía no
/// puede costar una consulta.
/// </summary>
public sealed class GeographyLookupTests
{
    [Fact]
    public async Task CustomerFindCitiesWithoutIdsDoesNotReachGeography()
    {
        var cities = new CountingCityRepository();
        var departments = new CountingDepartmentRepository();
        var lookup = new CustomerGeographyLookup(cities, departments, new CountingRegionRepository());

        var found = await lookup.FindCitiesAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
        Assert.Equal(0, cities.Calls);
        Assert.Equal(0, departments.Calls);
    }

    [Fact]
    public async Task CompanyFindCitiesWithoutIdsDoesNotReachGeography()
    {
        var cities = new CountingCityRepository();
        var departments = new CountingDepartmentRepository();
        var lookup = new CompanyGeographyLookup(cities, departments, new CountingRegionRepository());

        var found = await lookup.FindCitiesAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
        Assert.Equal(0, cities.Calls);
        Assert.Equal(0, departments.Calls);
    }

    // El camino con ids sigue igual: una consulta de ciudades y una de departamentos, sin importar
    // cuántas veces se repita la ciudad en la página.
    [Fact]
    public async Task CustomerFindCitiesResolvesRepeatedIdsWithOneQueryEach()
    {
        var antioquia = Department.Create(DepartmentId.New(), "05", "Antioquia");
        var medellin = City.Create(CityId.New(), "05001", "Medellín", antioquia.Id);
        var cities = new CountingCityRepository(medellin);
        var departments = new CountingDepartmentRepository(antioquia);
        var lookup = new CustomerGeographyLookup(cities, departments, new CountingRegionRepository());

        var found = await lookup.FindCitiesAsync(
            [medellin.Id.Value, medellin.Id.Value], TestContext.Current.CancellationToken);

        Assert.Equal("Antioquia", Assert.Single(found).Value.DepartmentName);
        Assert.Equal(1, cities.Calls);
        Assert.Equal(1, departments.Calls);
    }

    // Spec ReteICA por empresa §7.2: la tasa municipal se pinta con municipio y departamento, y una ciudad que no existe no
    // aparece (el handler la convierte en catalog.tax_rate.city_invalid).
    [Fact]
    public async Task TaxRateGeographyLookupReturnsNameAndDepartment()
    {
        var antioquia = Department.Create(DepartmentId.New(), "05", "Antioquia");
        var medellin = City.Create(CityId.New(), "05001", "Medellín", antioquia.Id);
        var cities = new CountingCityRepository(medellin);
        var departments = new CountingDepartmentRepository(antioquia);
        var lookup = new TaxRateGeographyLookup(cities, departments);

        var found = await lookup.FindCitiesAsync(
            [medellin.Id.Value, Guid.CreateVersion7()], TestContext.Current.CancellationToken);

        Assert.Equal(
            new TaxRateCityRef(medellin.Id.Value, "Medellín", antioquia.Id.Value, "Antioquia"),
            Assert.Single(found).Value);
        Assert.Empty(await lookup.FindCitiesAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CustomerFindRegionsWithoutIdsDoesNotReachGeography()
    {
        var regions = new CountingRegionRepository();
        var lookup = new CustomerGeographyLookup(
            new CountingCityRepository(), new CountingDepartmentRepository(), regions);

        var found = await lookup.FindRegionsAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
        Assert.Equal(0, regions.Calls);
    }

    [Fact]
    public async Task CustomerFindRegionsResolvesCountryAndNameWithOneQuery()
    {
        var madrid = Region.Create(RegionId.New(), "ES", "28", "Madrid");
        var regions = new CountingRegionRepository(madrid);
        var lookup = new CustomerGeographyLookup(
            new CountingCityRepository(), new CountingDepartmentRepository(), regions);

        var found = await lookup.FindRegionsAsync(
            [madrid.Id.Value, madrid.Id.Value], TestContext.Current.CancellationToken);

        var region = found[madrid.Id.Value];
        Assert.Equal(("ES", "Madrid"), (region.CountryCode, region.Name));
        Assert.Equal(1, regions.Calls);
    }

    [Fact]
    public async Task QuotationFindRegionsOmitsUnknownIds()
    {
        var madrid = Region.Create(RegionId.New(), "ES", "28", "Madrid");
        var regions = new CountingRegionRepository(madrid);
        var lookup = new QuotationGeographyLookup(
            new CountingCityRepository(), new CountingDepartmentRepository(), regions);

        var found = await lookup.FindRegionsAsync(
            [madrid.Id.Value, Guid.CreateVersion7()], TestContext.Current.CancellationToken);

        Assert.Equal(madrid.Id.Value, Assert.Single(found).Key);
    }
}
