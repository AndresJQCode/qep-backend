using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Infrastructure;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El importador de la lista de ciudades de Siigo corre en cada arranque. Cada prueba levanta su base: tocan la semilla y
/// no pueden compartir host.
/// </summary>
public sealed class BillingCitySeedTests
{
    private const int SiigoCities = 3482;

    [Fact]
    public async Task TheSiigoListIsSeededOnStartupWithoutColombia()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();

        Assert.Equal(SiigoCities, await dbContext.BillingCities.CountAsync(TestContext.Current.CancellationToken));
        Assert.False(await dbContext.BillingCities.AnyAsync(
            city => city.CountryCode == "CO", TestContext.Current.CancellationToken));
        var miami = await dbContext.BillingCities.SingleAsync(
            city => city.CountryCode == "US" && city.CityCode == "1", TestContext.Current.CancellationToken);
        Assert.Equal(
            ("Us", "09", "Florida", "Miami", "MIAMI"),
            (miami.SiigoCountryCode, miami.StateCode, miami.StateName, miami.CityName, miami.NormalizedName));
    }

    // Mismo hash guardado = la tabla no se lee: una edición por SQL, que el importador no puede ver sin leerla, sobrevive.
    [Fact]
    public async Task ReseedingWithTheSameFileDoesNotReadTheTable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(
            factory, "UPDATE geography.billing_cities SET city_name = 'Cambiada' WHERE country_code = 'US' AND city_code = '1'");

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Cambiada", await MiamiNameAsync(factory));
    }

    [Fact]
    public async Task ADifferentStoredHashRenamesButNeverDeletes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(
            factory, "UPDATE geography.billing_cities SET city_name = 'Cambiada' WHERE country_code = 'US' AND city_code = '1'");
        await ExecuteAsync(
            factory,
            """
            INSERT INTO geography.billing_cities
                (id, country_code, siigo_country_code, country_name, state_code, state_name, city_code, city_name, normalized_name)
            VALUES (gen_random_uuid(), 'US', 'Us', 'Estados Unidos', '99', 'X', '9999', 'Ficticia', 'FICTICIA')
            """);
        await ExecuteAsync(
            factory, "UPDATE geography.seed_state SET content_hash = 'viejo' WHERE key = 'billing-cities:siigo'");

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Miami", await MiamiNameAsync(factory));
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.True(await dbContext.BillingCities.AnyAsync(
            city => city.CityCode == "9999", TestContext.Current.CancellationToken));
        Assert.Equal(SiigoCities + 1, await dbContext.BillingCities.CountAsync(TestContext.Current.CancellationToken));
        var stored = await dbContext.Database
            .SqlQueryRaw<string>("SELECT content_hash AS \"Value\" FROM geography.seed_state WHERE key = 'billing-cities:siigo'")
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Matches("^[0-9a-f]{64}$", stored);
    }

    private static async Task ExecuteAsync(QepApiFactory factory, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task<string> MiamiNameAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        return await dbContext.BillingCities
            .Where(city => city.CountryCode == "US" && city.CityCode == "1")
            .Select(city => city.CityName)
            .SingleAsync(TestContext.Current.CancellationToken);
    }
}
