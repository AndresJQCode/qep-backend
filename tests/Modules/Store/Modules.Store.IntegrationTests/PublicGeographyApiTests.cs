using System.Net;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

public sealed class PublicGeographyApiTests
{
    [Fact]
    public async Task DepartmentsAreListedByNameWithoutDivipolaCodes()
    {
        await using var fixture = await OpenAsync();

        var response = await fixture.GetAsync("/geography/departments");
        var body = await StoreApiHarness.BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("divipolaCode", body, StringComparison.OrdinalIgnoreCase);
        var names = System.Text.Json.Nodes.JsonNode.Parse(body)!["items"]!.AsArray().Select(item => (string)item!["name"]!).ToList();
        Assert.NotEmpty(names);
        Assert.Equal(names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public async Task CitiesOfADepartmentAreListed()
    {
        await using var fixture = await OpenAsync();
        var departments = await fixture.GetJsonAsync("/geography/departments");

        System.Text.Json.Nodes.JsonNode? cities = null;
        foreach (var department in departments["items"]!.AsArray())
        {
            cities = await fixture.GetJsonAsync($"/geography/departments/{(Guid)department!["id"]!}/cities");
            if (cities["items"]!.AsArray().Count > 0)
            {
                break;
            }
        }

        var first = cities!["items"]!.AsArray()[0]!;
        Assert.NotEqual(Guid.Empty, (Guid)first["id"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)first["name"]));
    }

    [Fact]
    public async Task AnUnknownDepartmentIs404()
    {
        await using var fixture = await OpenAsync();

        var response = await fixture.GetAsync($"/geography/departments/{Guid.CreateVersion7()}/cities");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("store.public.department_not_found", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }

    [Fact]
    public async Task GeographyOfAnUnpublishedStoreIs404()
    {
        await using var fixture = await OpenAsync(publish: false);

        var response = await fixture.GetAsync("/geography/departments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("store.public.not_found", (string)(await PublicStoreFixture.JsonAsync(response))["code"]!);
    }

    [Fact]
    public async Task GeographyIsCachedForAnHour()
    {
        await using var fixture = await OpenAsync();

        var response = await fixture.GetAsync("/geography/departments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=3600, stale-while-revalidate=86400", response.Headers.CacheControl?.ToString());
    }
}
