using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El resolvedor del exterior sólo lee: empareja el texto escrito con la lista de Siigo por nombre normalizado, o por un
/// sinónimo vivo, y nunca elige entre dos ciudades que se llaman igual. Cada prueba levanta su base: unas escriben
/// sinónimos (datos globales) y otras cuentan filas.
/// </summary>
public sealed class ForeignBillingCityResolverTests
{
    [Theory]
    [InlineData("US", " miámi ", "Us", "09", "1")]
    [InlineData("US", "MIAMI", "Us", "09", "1")]
    [InlineData("us", " miámi ", "Us", "09", "1")] // el país llega como lo guardó el cliente: se normaliza aquí también
    [InlineData("EC", "Quito", "Ec", "20", "20075")] // la lista no se corrige: Quito queda en Pastaza
    public async Task AUniqueNameMatchesWhateverItsCaseAccentsOrSpaces(
        string country, string text, string siigoCountry, string state, string city)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var result = await ResolveAsync(factory, country, text);

        Assert.Equal(
            new ForeignCityResolution(
                ForeignCityResolutionKind.Matched, new BillingCityCodes(siigoCountry, state, city)),
            result);
    }

    [Theory]
    [InlineData("US", "Boston")]
    [InlineData("DE", "Berlín")]
    public async Task ARepeatedNameDoesNotMatchByItself(string country, string text)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var result = await ResolveAsync(factory, country, text);

        Assert.Equal(new ForeignCityResolution(ForeignCityResolutionKind.Unresolved, null), result);
    }

    [Fact]
    public async Task ARepeatedNameWithALiveSynonymMatchesTheCityTheOperatorChose()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await AddSynonymAsync(factory, "US", "BOSTON", "21", "02119");

        var result = await ResolveAsync(factory, "US", "Boston");

        Assert.Equal(
            new ForeignCityResolution(ForeignCityResolutionKind.Matched, new BillingCityCodes("Us", "21", "02119")),
            result);
    }

    [Fact]
    public async Task ALiveSynonymResolvesAndADeletedOneDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await AddSynonymAsync(factory, "US", "MIAMY", "09", "1");

        var live = await ResolveAsync(factory, "US", " Miamy ");
        await ExecuteAsync(factory, "UPDATE geography.foreign_city_synonyms SET deleted_at = now(), deleted_by = created_by");
        var deleted = await ResolveAsync(factory, "US", "Miamy");

        Assert.Equal(
            new ForeignCityResolution(ForeignCityResolutionKind.Matched, new BillingCityCodes("Us", "09", "1")), live);
        Assert.Equal(new ForeignCityResolution(ForeignCityResolutionKind.Unresolved, null), deleted);
    }

    [Theory]
    [InlineData("GD", "St. George's")]
    [InlineData("CO", "Bogotá")]
    public async Task ACountryOutsideTheCatalogIsCountryNotInCatalog(string country, string text)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var result = await ResolveAsync(factory, country, text);

        Assert.Equal(new ForeignCityResolution(ForeignCityResolutionKind.CountryNotInCatalog, null), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("́")]
    public async Task ABlankTextIsUnresolved(string text)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var result = await ResolveAsync(factory, "US", text);

        Assert.Equal(new ForeignCityResolution(ForeignCityResolutionKind.Unresolved, null), result);
    }

    [Fact]
    public async Task ResolvingNeverWrites()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var unresolved = await ResolveAsync(factory, "US", "Miamy");
        var repeated = await ResolveAsync(factory, "US", "Boston");

        Assert.Equal(ForeignCityResolutionKind.Unresolved, unresolved.Kind);
        Assert.Equal(ForeignCityResolutionKind.Unresolved, repeated.Kind);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Equal(0, await dbContext.UnresolvedForeignCities.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.UnresolvedForeignCityOccurrences.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.ForeignCitySynonyms.CountAsync(TestContext.Current.CancellationToken));
    }

    // Un scope por llamada, como cada pedido que arma Bootstrapper.
    private static async Task<ForeignCityResolution> ResolveAsync(QepApiFactory factory, string country, string text)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IForeignBillingCityResolver>()
            .ResolveAsync(country, text, TestContext.Current.CancellationToken);
    }

    // Un sinónimo vivo insertado por SQL, apuntando a la ciudad del catálogo con esos códigos.
    private static async Task AddSynonymAsync(
        QepApiFactory factory, string country, string normalizedText, string stateCode, string cityCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var operatorId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.foreign_city_synonyms
                 (id, country_code, normalized_text, display_text, billing_city_id,
                  created_by, created_at, updated_by, updated_at, version)
             SELECT {Guid.CreateVersion7()}, {country}, {normalizedText}, {normalizedText}, id,
                    {operatorId}, now(), {operatorId}, now(), 1
             FROM geography.billing_cities
             WHERE country_code = {country} AND state_code = {stateCode} AND city_code = {cityCode}
             """,
            TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(QepApiFactory factory, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }
}
