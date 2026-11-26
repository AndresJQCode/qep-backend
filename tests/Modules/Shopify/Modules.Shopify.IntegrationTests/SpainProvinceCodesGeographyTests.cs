using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary><see cref="SpainProvinceCodes"/> contra la semilla real de Geography: cada región de España sembrada tiene su código
/// ISO, y la tabla no nombra regiones que no existen. Una semilla que cambie rompe esta prueba y no el envío a Shopify en
/// silencio.</summary>
public sealed class SpainProvinceCodesGeographyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SpainProvinceCodesCoverEverySeededSpanishRegion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services; // el host migra y siembra Geography al arrancar.

        var seeded = (await ScalarAsync<string>(
            connectionString, "SELECT string_agg(code, '|' ORDER BY code) FROM geography.regions WHERE country_code = 'ES'")).Split('|');

        Assert.Equal(seeded, SpainProvinceCodes.All.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    // El puerto que usan el worker y el guardado: la región de Geography con su país y su código INE.
    [Fact]
    public async Task TheGeographyPortDescribesASeededRegion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IShopifyGeographyLookup>();
        var madrid = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM geography.regions WHERE country_code = 'ES' AND code = '28'");

        Assert.Equal(("ES", "28"), await lookup.DescribeRegionAsync(madrid, Ct));
        Assert.Null(await lookup.DescribeRegionAsync(Guid.CreateVersion7(), Ct));
    }
}
