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

    [Fact]
    public async Task ReseedingRestoresARenamedRegion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE geography.regions SET name = 'Cambiada' WHERE code = '28'",
                TestContext.Current.CancellationToken);
        }

        await factory.Services.InitializeGeographyDatabaseAsync(TestContext.Current.CancellationToken);

        await using var checkScope = factory.Services.CreateAsyncScope();
        var checkContext = checkScope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var madrid = await checkContext.Regions.SingleAsync(
            region => region.Code == "28", TestContext.Current.CancellationToken);
        Assert.Equal("Madrid", madrid.Name);
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
