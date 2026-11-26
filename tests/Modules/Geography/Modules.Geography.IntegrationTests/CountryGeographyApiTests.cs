using System.Net;
using System.Net.Http.Json;
using Modules.Geography.Api;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>Regiones y ciudades por país (spec 2026-11-20): un solo flujo para el frontend, sea cual sea el país.</summary>
public sealed class CountryGeographyApiTests
{
    [Fact]
    public async Task CountriesWithoutAuthIsUnauthorized()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/countries", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnlyColombiaAndSpainCarryARegionKind()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var countries = await GetAsync<CountryResponse>(client, "/api/v1/countries");

        Assert.Equal(
            [("CO", "Department"), ("ES", "Province")],
            countries
                .Where(country => country.RegionKind is not null)
                .Select(country => (country.Code, country.RegionKind))
                .Order());
    }

    [Fact]
    public async Task ColombiaRegionsAreTheDivipolaDepartments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var regions = await GetAsync<RegionResponse>(client, "/api/v1/countries/CO/regions");
        var departments = await GetAsync<DepartmentResponse>(client, "/api/v1/departments");

        Assert.Equal(33, regions.Length);
        Assert.Contains(regions, region => region.Code == "05" && region.Name == "ANTIOQUIA");
        Assert.Equal(
            departments.Select(department => department.Id).Order(),
            regions.Select(region => region.Id).Order());
    }

    [Fact]
    public async Task ColombiaCitiesUseTheDivipolaCityIds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);
        var antioquia = (await GetAsync<RegionResponse>(client, "/api/v1/countries/CO/regions"))
            .Single(region => region.Code == "05");

        var cities = await GetAsync<RegionCityResponse>(
            client, $"/api/v1/countries/CO/regions/{antioquia.Id}/cities");
        var divipolaCities = await GetAsync<CityResponse>(
            client, $"/api/v1/cities?departmentId={antioquia.Id}");

        Assert.Equal(125, cities.Length);
        Assert.Contains(cities, city => city.Code == "05001" && city.Name == "MEDELLÍN");
        Assert.Equal(
            divipolaCities.Select(city => city.Id).Order(),
            cities.Select(city => city.Id).Order());
    }

    [Fact]
    public async Task SpainRegionsAreTheFiftyTwoProvinces()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var regions = await GetAsync<RegionResponse>(client, "/api/v1/countries/ES/regions");

        Assert.Equal(52, regions.Length);
        Assert.Contains(regions, region => region.Code == "28" && region.Name == "Madrid");
        Assert.Equal(
            regions.Select(region => region.Name).Order(StringComparer.OrdinalIgnoreCase),
            regions.Select(region => region.Name));
    }

    [Fact]
    public async Task SpainCitiesOfMadridContainMadrid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);
        var madrid = (await GetAsync<RegionResponse>(client, "/api/v1/countries/ES/regions"))
            .Single(region => region.Code == "28");

        var cities = await GetAsync<RegionCityResponse>(
            client, $"/api/v1/countries/ES/regions/{madrid.Id}/cities");

        Assert.Contains(cities, city => city.Code == "28079" && city.Name == "Madrid");
        Assert.All(cities, city => Assert.StartsWith("28", city.Code, StringComparison.Ordinal));
        // El nombre como se conoce, no el del INE ("Rozas de Madrid, Las").
        Assert.Contains(cities, city => city.Code == "28127" && city.Name == "Las Rozas de Madrid");
    }

    [Fact]
    public async Task CountryIsCaseAndSpaceInsensitive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var canonical = await GetAsync<RegionResponse>(client, "/api/v1/countries/ES/regions");
        var sloppy = await GetAsync<RegionResponse>(client, "/api/v1/countries/%20es%20/regions");

        Assert.Equal(canonical.Select(region => region.Id), sloppy.Select(region => region.Id));
    }

    [Fact]
    public async Task ACountryWithoutCatalogReturnsEmptyRegions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var regions = await GetAsync<RegionResponse>(client, "/api/v1/countries/US/regions");

        Assert.Empty(regions);
    }

    [Fact]
    public async Task AColombianRegionUnderSpainIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);
        var antioquia = (await GetAsync<RegionResponse>(client, "/api/v1/countries/CO/regions"))
            .Single(region => region.Code == "05");

        var response = await client.GetAsync(
            $"/api/v1/countries/ES/regions/{antioquia.Id}/cities", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "geography.region.not_found");
    }

    [Fact]
    public async Task ASpanishRegionUnderColombiaIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);
        var madrid = (await GetAsync<RegionResponse>(client, "/api/v1/countries/ES/regions"))
            .Single(region => region.Code == "28");

        var response = await client.GetAsync(
            $"/api/v1/countries/CO/regions/{madrid.Id}/cities", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "geography.region.not_found");
    }

    [Fact]
    public async Task AnUnknownRegionIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var response = await client.GetAsync(
            $"/api/v1/countries/ES/regions/{Guid.NewGuid()}/cities", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "geography.region.not_found");
    }

    [Fact]
    public async Task ARegionOfACountryWithoutCatalogIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var response = await client.GetAsync(
            $"/api/v1/countries/US/regions/{Guid.NewGuid()}/cities", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "geography.region.not_found");
    }

    private static async Task<T[]> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<T[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }
}
