using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// <see cref="ICityRepository.FindByNameAsync"/> es la que resuelve la ciudad escrita a mano en el
/// Excel de importación de clientes. Desde que algunas ciudades tienen nombre común, la persona
/// puede escribir cualquiera de los dos: el común ("Cali") o el oficial del DANE
/// ("Santiago de Cali"), y los dos tienen que llegar a la misma ciudad. Desde los sinónimos
/// (spec 2026-10-26 §4.1), <see cref="ICityRepository.MatchByNameAsync"/> además reconoce un texto que
/// un operador confirmó ("Calli") y dice cuál sinónimo lo resolvió.
/// </summary>
public sealed class CityNameLookupTests
{
    private static readonly Guid OperatorId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

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

    [Fact]
    public async Task MatchByNameFindsAnActiveSynonymAndSaysWhichOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var synonymId = await AddSynonymAsync(factory, "76", "Calli", "76001");

        var match = await MatchAsync(factory, "76", "calli ");

        Assert.NotNull(match);
        Assert.Equal("76001", match.City.DivipolaCode);
        Assert.Equal(synonymId, match.SynonymId);
    }

    [Fact]
    public async Task ADeletedSynonymNoLongerMatches()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var synonymId = await AddSynonymAsync(factory, "76", "Calli", "76001");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var synonyms = scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>();
            var synonym = await synonyms.FindAsync(synonymId, TestContext.Current.CancellationToken);
            Assert.NotNull(synonym);
            synonym.Delete(OperatorId, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var match = await MatchAsync(factory, "76", "Calli");

        Assert.Null(match);
    }

    // Review Focus 1: una fila de sinónimo con el texto de una ciudad real (sólo puede llegar por SQL, la
    // API la rechazaría) no le gana a la ciudad.
    [Fact]
    public async Task ARealCityNameWinsOverASynonymWithTheSameText()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
            var department = await DepartmentAsync(dbContext, "76");
            var cali = await CityAsync(dbContext, "76001");
            var now = DateTimeOffset.UtcNow;
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO geography.city_synonyms
                    (id, department_id, normalized_text, display_text, city_id, created_by, created_at, updated_by, updated_at, version)
                VALUES ({Guid.CreateVersion7()}, {department.Id.Value}, 'CALIMA', 'Calima', {cali.Id.Value},
                        {OperatorId}, {now}, {OperatorId}, {now}, 1)
                """,
                TestContext.Current.CancellationToken);
        }

        var match = await MatchAsync(factory, "76", "Calima");

        Assert.NotNull(match);
        Assert.Equal("76126", match.City.DivipolaCode);
        Assert.Null(match.SynonymId);
    }

    [Fact]
    public async Task ASynonymOfAnotherDepartmentDoesNotMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await AddSynonymAsync(factory, "05", "Calli", "05001");

        var match = await MatchAsync(factory, "76", "Calli");

        Assert.Null(match);
    }

    // D-C1: la normalización colapsa los espacios internos, así que un nombre con espacios de más sigue
    // siendo la ciudad.
    [Fact]
    public async Task InternalSpacesCollapse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var match = await MatchAsync(factory, "76", "Santiago   de  Cali");

        Assert.NotNull(match);
        Assert.Equal("76001", match.City.DivipolaCode);
        Assert.Null(match.SynonymId);
    }

    private static async Task<CitySynonymId> AddSynonymAsync(
        QepApiFactory factory, string departmentCode, string text, string cityCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await DepartmentAsync(dbContext, departmentCode);
        var city = await CityAsync(dbContext, cityCode);
        var synonym = CitySynonym.Create(
            CitySynonymId.New(), department.Id, text, city.Id, OperatorId, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>().Add(synonym);
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return synonym.Id;
    }

    private static async Task<CityNameMatch?> MatchAsync(QepApiFactory factory, string departmentCode, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await DepartmentAsync(dbContext, departmentCode);
        return await scope.ServiceProvider.GetRequiredService<ICityRepository>()
            .MatchByNameAsync(department.Id, name, TestContext.Current.CancellationToken);
    }

    private static Task<Department> DepartmentAsync(GeographyDbContext dbContext, string code) =>
        dbContext.Departments.SingleAsync(value => value.DivipolaCode == code, TestContext.Current.CancellationToken);

    private static Task<City> CityAsync(GeographyDbContext dbContext, string code) =>
        dbContext.Cities.SingleAsync(value => value.DivipolaCode == code, TestContext.Current.CancellationToken);
}
