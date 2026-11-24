using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Infrastructure;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>El importador de regiones corre en cada arranque, junto al de DIVIPOLA: tiene que ser idempotente.</summary>
public sealed class RegionSeedTests
{
    private const int SpainRegions = 52;
    private const int SpainCities = 8132;

    [Fact]
    public async Task SpainIsSeededOnStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();

        var regions = await dbContext.Regions.ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SpainRegions, regions.Length);
        Assert.All(regions, region => Assert.Equal("ES", region.CountryCode));
        var madrid = Assert.Single(regions, region => region.Code == "28");
        Assert.Equal("Madrid", madrid.Name);
        Assert.Equal(SpainCities, await dbContext.RegionCities.CountAsync(TestContext.Current.CancellationToken));
        Assert.True(await dbContext.RegionCities.AnyAsync(
            city => city.RegionId == madrid.Id && city.Code == "28079" && city.Name == "Madrid",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReseedingDoesNotDuplicateRegionsOrCities()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Equal(SpainRegions, await dbContext.Regions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SpainCities, await dbContext.RegionCities.CountAsync(TestContext.Current.CancellationToken));
    }

    // Mismo hash guardado = el país no se vuelve a leer: ni filas en memoria ni comparación. Se prueba porque
    // una edición por SQL (que el importador no puede ver sin leer la tabla) sobrevive al re-arranque.
    [Fact]
    public async Task ReseedingWithTheSameDataDoesNotReadTheTables()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(factory, "UPDATE geography.regions SET name = 'Cambiada' WHERE code = '28'");

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Cambiada", await RegionNameAsync(factory, "28"));
    }

    [Fact]
    public async Task ADifferentStoredHashReconcilesRenamedRegionsAndCities()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(factory, "UPDATE geography.regions SET name = 'Cambiada' WHERE code = '28'");
        await ExecuteAsync(factory, "UPDATE geography.region_cities SET name = 'Cambiada' WHERE code = '28079'");
        await ExecuteAsync(factory, "UPDATE geography.seed_state SET content_hash = 'viejo' WHERE key = 'regions:ES'");

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Madrid", await RegionNameAsync(factory, "28"));
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Equal(
            "Madrid",
            await dbContext.RegionCities.Where(city => city.Code == "28079").Select(city => city.Name)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SpainRegions, await dbContext.Regions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SpainCities, await dbContext.RegionCities.CountAsync(TestContext.Current.CancellationToken));
        var stored = await dbContext.Database
            .SqlQueryRaw<string>("SELECT content_hash AS \"Value\" FROM geography.seed_state WHERE key = 'regions:ES'")
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Matches("^[0-9a-f]{64}$", stored);
    }

    [Fact]
    public async Task AMissingStateRowWithDataAlreadyThereDoesNotDuplicateAnything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(factory, "DELETE FROM geography.seed_state");

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Equal(SpainRegions, await dbContext.Regions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SpainCities, await dbContext.RegionCities.CountAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteAsync(QepApiFactory factory, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task<string> RegionNameAsync(QepApiFactory factory, string code)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        return await dbContext.Regions.Where(region => region.Code == code).Select(region => region.Name)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ColombiaIsNotCopiedToRegions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();

        Assert.False(await dbContext.Regions.AnyAsync(
            region => region.CountryCode == "CO", TestContext.Current.CancellationToken));
    }
}
