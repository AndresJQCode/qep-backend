using System.Net;
using System.Net.Http.Json;
using Modules.Geography.Api;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

public sealed class GeographyApiTests
{
    [Fact]
    public async Task ListDepartmentsWithoutAuthHeadersIsUnauthorized()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/departments", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListDepartmentsReturnsAllThirtyThreeSeededDepartments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var response = await client.GetAsync(
            "/api/v1/departments", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var departments = await response.Content.ReadFromJsonAsync<DepartmentResponse[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        Assert.Equal(33, departments.Length);
        Assert.Contains(
            departments,
            department => department.DivipolaCode == "05" && department.Name == "ANTIOQUIA");
    }

    [Fact]
    public async Task ListCitiesWithoutDepartmentIdIsBadRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var response = await client.GetAsync(
            "/api/v1/cities", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListCitiesForAntioquiaReturnsAllEightHundredNinetySixCities()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);
        var antioquiaId = await GetAntioquiaIdAsync(client);

        var response = await client.GetAsync(
            $"/api/v1/cities?departmentId={antioquiaId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cities = await response.Content.ReadFromJsonAsync<CityResponse[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(cities);
        // 896 = 125 municipios (código de 5 dígitos) + 771 centros poblados/corregimientos
        // (código de 8 dígitos) de Antioquia.
        Assert.Equal(896, cities.Length);
        Assert.All(cities, city => Assert.Equal(antioquiaId, city.DepartmentId));
        Assert.Contains(
            cities, city => city.DivipolaCode == "05001" && city.Name == "MEDELLÍN");
    }

    [Fact]
    public async Task ListCitiesForAnUnknownDepartmentReturnsAnEmptyArray()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAuthenticatedClient(factory);

        var response = await client.GetAsync(
            $"/api/v1/cities?departmentId={Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cities = await response.Content.ReadFromJsonAsync<CityResponse[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(cities);
        Assert.Empty(cities);
    }

    private static async Task<Guid> GetAntioquiaIdAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var departments = await response.Content.ReadFromJsonAsync<DepartmentResponse[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        return departments.Single(department => department.DivipolaCode == "05").Id;
    }
}
