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
