using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>Spec 2026-10-15 §6.2.1 paso 5 contra la semilla real de Geography: cada código de <see cref="ColombiaProvinceCodes"/>
/// nombra un departamento sembrado, con su nombre exacto, y la ciudad se resuelve sin importar tildes ni mayúsculas (lo que Shopify
/// entrega lo escribió el comprador).</summary>
public sealed class ColombiaProvinceCodesGeographyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryCodeResolvesToSeededDepartment()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services; // el host migra y siembra Geography al arrancar.

        var seeded = (await ScalarAsync<string>(
            connectionString, "SELECT string_agg(name, '|' ORDER BY name) FROM geography.departments")).Split('|');

        Assert.Equal(seeded, ColombiaProvinceCodes.All.Values.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("DC", "Bogota")]
    [InlineData("ANT", "MEDELLIN")]
    [InlineData("ant", "medellín")]
    [InlineData("VAC", "Cali")]
    public async Task CityResolvesIgnoringAccentsAndCase(string provinceCode, string city)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IShopifyGeographyLookup>();

        var department = ColombiaProvinceCodes.DepartmentNameFor(provinceCode);

        Assert.NotNull(department);
        Assert.NotNull((await lookup.LookupCityAsync(department, city, Ct)).City);
    }
}
