using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El reportero del exterior escribe la bandeja: una fila por país y texto normalizado, una ocurrencia por pedido. Una orden
/// vista muchas veces (vista previa repetida, vista previa y luego tanda) deja una sola ocurrencia y no mueve la fila.
/// Cada prueba levanta su base: escriben datos globales.
/// </summary>
public sealed class ForeignCityReporterTests
{
    private static readonly Guid ReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000bb");
    private static readonly DateTimeOffset Seen = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFirstReportCreatesAPendingRowWithItsOccurrence()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var report = Report(" Miamy ");

        var outcome = await ReportAsync(factory, report);

        Assert.Equal(ForeignCityReportOutcome.Recorded, outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal(("US", "MIAMY", "Miamy", UnresolvedCityStatus.Pending), (row.CountryCode, row.NormalizedText, row.CityText, row.Status));
        Assert.Null(row.SynonymId);
        Assert.Equal((Seen, Seen), (row.FirstSeenAt, row.LastSeenAt));
        var occurrence = Assert.Single(await OccurrencesAsync(dbContext));
        Assert.Equal(
            (row.Id, ReportTenantId, report.OrderId, Seen),
            (occurrence.UnresolvedCityId, occurrence.TenantId, occurrence.OrderId, occurrence.SeenAt));
    }

    [Fact]
    public async Task TheSameOrderReportedAgainIsAlreadyRecordedAndDoesNotMoveTheRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var report = Report("Miamy");
        await ReportAsync(factory, report);

        var second = await ReportAsync(factory, report with { SeenAt = Seen.AddHours(1) });

        Assert.Equal(ForeignCityReportOutcome.AlreadyRecorded, second);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal((Seen, Seen), (row.FirstSeenAt, row.LastSeenAt));
        var occurrence = Assert.Single(await OccurrencesAsync(dbContext));
        Assert.Equal(Seen, occurrence.SeenAt);
    }

    [Fact]
    public async Task TwoOrdersOfTheSameTextAreOneRowWithTwoOccurrences()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var first = await ReportAsync(factory, Report("Miamy"));
        var second = await ReportAsync(factory, Report("MIÁMY ") with { SeenAt = Seen.AddHours(2) });

        Assert.Equal([ForeignCityReportOutcome.Recorded, ForeignCityReportOutcome.Recorded], [first, second]);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal((Seen, Seen.AddHours(2)), (row.FirstSeenAt, row.LastSeenAt));
        var occurrences = await OccurrencesAsync(dbContext);
        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, occurrence => Assert.Equal(row.Id, occurrence.UnresolvedCityId));
    }

    [Fact]
    public async Task ADiscardedTextAccumulatesAndStaysDiscarded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ReportAsync(factory, Report("Miamy"));
        await ExecuteAsync(factory, "UPDATE geography.unresolved_foreign_cities SET status = 'Discarded'");

        var outcome = await ReportAsync(factory, Report("Miamy") with { SeenAt = Seen.AddHours(1) });

        Assert.Equal(ForeignCityReportOutcome.Recorded, outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal((UnresolvedCityStatus.Discarded, Seen.AddHours(1)), (row.Status, row.LastSeenAt));
        Assert.Equal(2, (await OccurrencesAsync(dbContext)).Count);
    }

    [Fact]
    public async Task AnActiveSynonymAnswersSynonymExistsWithoutWriting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await ExecuteAsync(
            factory,
            """
            INSERT INTO geography.foreign_city_synonyms
                (id, country_code, normalized_text, display_text, billing_city_id,
                 created_by, created_at, updated_by, updated_at, version)
            SELECT gen_random_uuid(), 'US', 'MIAMY', 'Miamy', id,
                   gen_random_uuid(), now(), gen_random_uuid(), now(), 1
            FROM geography.billing_cities WHERE country_code = 'US' AND city_code = '1'
            """);

        var outcome = await ReportAsync(factory, Report("miamy "));

        Assert.Equal(ForeignCityReportOutcome.SynonymExists, outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Empty(await RowsAsync(dbContext));
        Assert.Empty(await OccurrencesAsync(dbContext));
    }

    [Fact]
    public async Task ACountryOutsideTheCatalogWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var outcome = await ReportAsync(factory, Report("St. George's") with { CountryCode = "GD" });

        Assert.Equal(ForeignCityReportOutcome.CountryNotInCatalog, outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Empty(await RowsAsync(dbContext));
        Assert.Empty(await OccurrencesAsync(dbContext));
    }

    [Theory]
    [InlineData("")]
    [InlineData("́")]
    public async Task ABlankTextIsIgnored(string text)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();

        var outcome = await ReportAsync(factory, Report(text));

        Assert.Equal(ForeignCityReportOutcome.Ignored, outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Empty(await RowsAsync(dbContext));
        Assert.Empty(await OccurrencesAsync(dbContext));
    }

    private static ForeignCityReport Report(string cityText) =>
        new(ReportTenantId, Guid.CreateVersion7(), "US", cityText, Seen);

    // Un scope por reporte, como cada vista previa o emisión de Bootstrapper.
    private static async Task<ForeignCityReportOutcome> ReportAsync(QepApiFactory factory, ForeignCityReport report)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IForeignCityReporter>()
            .ReportAsync(report, TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(QepApiFactory factory, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static Task<List<UnresolvedForeignCity>> RowsAsync(GeographyDbContext dbContext) =>
        dbContext.UnresolvedForeignCities.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

    private static Task<List<UnresolvedForeignCityOccurrence>> OccurrencesAsync(GeographyDbContext dbContext) =>
        dbContext.UnresolvedForeignCityOccurrences.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
}
