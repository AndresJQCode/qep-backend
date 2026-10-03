using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El importador corre en cada arranque de la app (<c>InitializeGeographyDatabaseAsync</c>), así
/// que tiene que ser idempotente: un segundo arranque contra la misma base no puede duplicar filas
/// ni violar el índice único de <c>divipola_code</c>.
/// </summary>
public sealed class GeographySeedIntegrityTests
{
    [Fact]
    public async Task ReseedingDoesNotChangeTheDepartmentOrCityCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Fuerza a que el host arranque (y con él, la migración + el primer seed).
        using var warmUpClient = factory.CreateClient();

        await factory.Services.InitializeGeographyDatabaseAsync(
            TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var departmentCount = await dbContext.Departments.CountAsync(
            TestContext.Current.CancellationToken);
        var cityCount = await dbContext.Cities.CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(33, departmentCount);
        Assert.Equal(1122, cityCount);
    }

    // El nombre de Coordinadora sale del snapshot embebido en cada arranque. 05002 está en el
    // snapshot; 13030 (ALTOS DEL ROSARIO) Coordinadora no lo lista, así que queda null.
    [Fact]
    public async Task ReseedingSetsTheCoordinadoraNameFromTheSnapshotWithoutChangingTheCityCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Fuerza a que el host arranque (y con él, la migración + el primer seed).
        using var warmUpClient = factory.CreateClient();

        await factory.Services.InitializeGeographyDatabaseAsync(
            TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var cityCount = await dbContext.Cities.CountAsync(TestContext.Current.CancellationToken);
        var abejorral = await dbContext.Cities.SingleAsync(
            city => city.DivipolaCode == "05002", TestContext.Current.CancellationToken);
        var altosDelRosario = await dbContext.Cities.SingleAsync(
            city => city.DivipolaCode == "13030", TestContext.Current.CancellationToken);

        Assert.Equal(1122, cityCount);
        Assert.Equal("ABEJORRAL (ANT)", abejorral.CoordinadoraName);
        Assert.Null(altosDelRosario.CoordinadoraName);
    }

    // Cada arranque reconcilia contra el snapshot: un nombre que alguien cambió a mano vuelve al
    // del snapshot, y uno puesto en una ciudad que el snapshot no lista se borra.
    [Fact]
    public async Task ReseedingReconcilesCoordinadoraNamesThatDriftedFromTheSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        await using (var driftScope = factory.Services.CreateAsyncScope())
        {
            var dbContext = driftScope.ServiceProvider.GetRequiredService<GeographyDbContext>();
            var abejorral = await dbContext.Cities.SingleAsync(
                city => city.DivipolaCode == "05002", TestContext.Current.CancellationToken);
            var altosDelRosario = await dbContext.Cities.SingleAsync(
                city => city.DivipolaCode == "13030", TestContext.Current.CancellationToken);
            abejorral.SetCoordinadoraName("OTRO NOMBRE");
            altosDelRosario.SetCoordinadoraName("ALTOS DEL ROSARIO (BOL)");
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await factory.Services.InitializeGeographyDatabaseAsync(
            TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var reseeded = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var names = await reseeded.Cities
            .Where(city => city.DivipolaCode == "05002" || city.DivipolaCode == "13030")
            .ToDictionaryAsync(
                city => city.DivipolaCode,
                city => city.CoordinadoraName,
                TestContext.Current.CancellationToken);

        Assert.Equal("ABEJORRAL (ANT)", names["05002"]);
        Assert.Null(names["13030"]);
    }

    [Fact]
    public async Task InsertingTwoDepartmentsWithTheSameDivipolaCodeViolatesTheUniqueIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Fuerza a que el host arranque y aplique las migraciones antes de escribir a mano.
        using var warmUpClient = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        dbContext.Departments.Add(Department.Create(DepartmentId.New(), "99", "PRIMERO"));
        dbContext.Departments.Add(Department.Create(DepartmentId.New(), "99", "SEGUNDO"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
