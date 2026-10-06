using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// <see cref="ICityRepository.FindByNameAsync"/> es la que resuelve la ciudad escrita a mano en el
/// Excel de importación de clientes. Desde que algunas ciudades tienen nombre común, la persona
/// puede escribir cualquiera de los dos: el común ("Cali") o el oficial del DANE
/// ("Santiago de Cali"), y los dos tienen que llegar a la misma ciudad.
/// </summary>
public sealed class CityNameLookupTests
{
    [Theory]
    [InlineData("76", "Cali", "76001")]
    [InlineData("76", "Santiago de Cali", "76001")]
    [InlineData("76", "SANTIAGO DE CALI", "76001")]
    [InlineData("11", "Bogota", "11001")]
    [InlineData("11", "Bogotá, D.C.", "11001")]
    [InlineData("05", "Medellin", "05001")]
    public async Task FindByNameResolvesBothTheCommonAndTheDaneName(
        string departmentCode, string name, string expectedCityCode)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Fuerza a que el host arranque (y con él, la migración + el seed).
        using var warmUpClient = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await dbContext.Departments.SingleAsync(
            value => value.DivipolaCode == departmentCode, TestContext.Current.CancellationToken);
        var repository = scope.ServiceProvider.GetRequiredService<ICityRepository>();

        var city = await repository.FindByNameAsync(
            department.Id, name, TestContext.Current.CancellationToken);

        Assert.NotNull(city);
        Assert.Equal(expectedCityCode, city.DivipolaCode);
    }
}
