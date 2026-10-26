using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El reporte de una ciudad sin resolver (spec 2026-10-26 §4.2, SC-03): una fila por texto normalizado y departamento,
/// una ocurrencia por fila de origen. Lo llaman la importación de Shopify (SC-06) y el recorrido histórico (SC-13), que
/// pueden reintentar la misma fila de origen, así que todo tiene que ser idempotente.
/// </summary>
public sealed class UnresolvedCityReporterTests
{
    private static readonly Guid OperatorId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
    private static readonly Guid ReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000bb");
    private static readonly DateTimeOffset Seen = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFirstReportCreatesAPendingRowWithItsOccurrence()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var report = Report(department, " Calli ") with
        {
            CustomerId = Guid.CreateVersion7(),
            ProvinceCode = "VAC",
            PartyCityOnly = true,
            CustomerCityOnly = true,
        };

        var result = await ReportAsync(factory, report);

        Assert.Equal(UnresolvedCityReportOutcome.Recorded, result.Outcome);
        Assert.NotNull(result.UnresolvedCityId);
        Assert.Null(result.SynonymId);
        Assert.Null(result.CityId);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal(result.UnresolvedCityId, row.Id);
        Assert.Equal(department, row.DepartmentId);
        Assert.Equal("CALLI", row.NormalizedText);
        Assert.Equal("Calli", row.CityText);
        Assert.Equal(report.SampleAddress, row.SampleAddress);
        Assert.Equal("VAC", row.ProvinceCode);
        Assert.Equal(UnresolvedCityStatus.Pending, row.Status);
        Assert.Null(row.SynonymId);
        Assert.Equal(Seen, row.FirstSeenAt);
        Assert.Equal(Seen, row.LastSeenAt);
        var occurrence = Assert.Single(await OccurrencesAsync(dbContext));
        Assert.Equal(row.Id, occurrence.UnresolvedCityId);
        Assert.Equal(ReportTenantId, occurrence.TenantId);
        Assert.Equal("shopify", occurrence.Source);
        Assert.Equal(report.SourceRef, occurrence.SourceRef);
        Assert.Equal(report.OrderId, occurrence.OrderId);
        Assert.Equal(report.CustomerId, occurrence.CustomerId);
        Assert.True(occurrence.PartyCityOnly);
        Assert.True(occurrence.CustomerCityOnly);
        Assert.Equal(Seen, occurrence.SeenAt);
    }

    [Fact]
    public async Task FiveOrdersOfTheSameTextAreOneRowWithFiveOccurrences()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        string[] texts = ["Calli", "CALLI ", "calli", " Calli", "CALLÍ"];

        var results = new List<UnresolvedCityReportResult>();
        for (var index = 0; index < texts.Length; index++)
        {
            results.Add(await ReportAsync(factory, Report(department, texts[index]) with
            {
                SampleAddress = $"Dirección {index}",
                SeenAt = Seen.AddHours(index),
            }));
        }

        Assert.All(results, result => Assert.Equal(UnresolvedCityReportOutcome.Recorded, result.Outcome));
        Assert.Single(results.Select(result => result.UnresolvedCityId).Distinct());
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal("Dirección 4", row.SampleAddress);
        Assert.Equal(Seen, row.FirstSeenAt);
        Assert.Equal(Seen.AddHours(4), row.LastSeenAt);
        Assert.Equal(5, (await OccurrencesAsync(dbContext)).Count(occurrence => occurrence.UnresolvedCityId == row.Id));
    }

    // Review Focus 2: el webhook y la conciliación pueden reportar la misma fila del espejo a la vez. Un tercero toma
    // el candado del texto primero, así los dos reportes quedan esperándolo al mismo tiempo antes de soltarlos.
    [Fact]
    public async Task TheSameMirrorRowReportedTwiceConcurrentlyLeavesOneOccurrence()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var report = Report(department, "Calli");
        var cancellationToken = TestContext.Current.CancellationToken;

        Task<UnresolvedCityReportResult>[] reports;
        await using (var holder = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = holder.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await unitOfWork.LockCityTextAsync(department, "CALLI", cancellationToken);

            reports = [ReportAsync(factory, report), ReportAsync(factory, report)];
            await WaitForAdvisoryLockWaitersAsync(holder.ServiceProvider.GetRequiredService<GeographyDbContext>(), 2);
        }

        var results = await Task.WhenAll(reports);

        Assert.Equal(
            [UnresolvedCityReportOutcome.Recorded, UnresolvedCityReportOutcome.AlreadyRecorded],
            results.Select(result => result.Outcome).Order());
        Assert.Single(results.Select(result => result.UnresolvedCityId).Distinct());
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Single(await RowsAsync(dbContext));
        Assert.Single(await OccurrencesAsync(dbContext));
    }

    // Si la misma fila de origen vuelve con otro texto (se editó la ciudad en la tienda), la ocurrencia ya existe: no
    // queda una fila nueva sin ocurrencias.
    [Fact]
    public async Task TheSameMirrorRowWithAnotherTextIsAlreadyRecordedAndOpensNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var report = Report(department, "Calli");
        var first = await ReportAsync(factory, report);

        var second = await ReportAsync(factory, report with { CityText = "Kali" });

        Assert.Equal(UnresolvedCityReportOutcome.AlreadyRecorded, second.Outcome);
        Assert.Equal(first.UnresolvedCityId, second.UnresolvedCityId);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Equal("CALLI", Assert.Single(await RowsAsync(dbContext)).NormalizedText);
        Assert.Single(await OccurrencesAsync(dbContext));
    }

    [Fact]
    public async Task ADiscardedTextAccumulatesAndStaysDiscarded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var first = await ReportAsync(factory, Report(department, "Calli"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
            var row = await dbContext.UnresolvedCities.SingleAsync(TestContext.Current.CancellationToken);
            row.Discard(Seen.AddMinutes(1));
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await ReportAsync(factory, Report(department, "calli") with { SeenAt = Seen.AddHours(1) });

        Assert.Equal(UnresolvedCityReportOutcome.Recorded, second.Outcome);
        Assert.Equal(first.UnresolvedCityId, second.UnresolvedCityId);
        await using var readScope = factory.Services.CreateAsyncScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var discarded = Assert.Single(await RowsAsync(readContext));
        Assert.Equal(UnresolvedCityStatus.Discarded, discarded.Status);
        Assert.Equal(Seen.AddHours(1), discarded.LastSeenAt);
        Assert.Equal(2, (await OccurrencesAsync(readContext)).Count);
    }

    [Fact]
    public async Task AnActiveSynonymAnswersSynonymExistsWithoutWriting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var (synonymId, caliId) = await AddSynonymAsync(factory, department, "Calli");

        var result = await ReportAsync(factory, Report(department, "CALLI "));

        Assert.Equal(UnresolvedCityReportOutcome.SynonymExists, result.Outcome);
        Assert.Null(result.UnresolvedCityId);
        Assert.Equal(synonymId, result.SynonymId);
        Assert.Equal(caliId, result.CityId);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Empty(await RowsAsync(dbContext));
        Assert.Empty(await OccurrencesAsync(dbContext));
    }

    [Fact]
    public async Task AfterDeletingTheSynonymTheTextOpensANewPendingRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var first = await ReportAsync(factory, Report(department, "Calli"));
        var (synonymId, _) = await AddSynonymAsync(factory, department, "Calli");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
            var row = await dbContext.UnresolvedCities.SingleAsync(TestContext.Current.CancellationToken);
            row.Resolve(synonymId, Seen.AddMinutes(1));
            var synonym = await dbContext.CitySynonyms.SingleAsync(TestContext.Current.CancellationToken);
            synonym.Delete(OperatorId, Seen.AddMinutes(2));
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await ReportAsync(factory, Report(department, "Calli") with { SeenAt = Seen.AddHours(1) });

        Assert.Equal(UnresolvedCityReportOutcome.Recorded, second.Outcome);
        Assert.NotEqual(first.UnresolvedCityId, second.UnresolvedCityId);
        await using var readScope = factory.Services.CreateAsyncScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var rows = await RowsAsync(readContext);
        var resolved = Assert.Single(rows, row => row.Id == first.UnresolvedCityId);
        Assert.Equal(UnresolvedCityStatus.Resolved, resolved.Status);
        Assert.Equal(synonymId, resolved.SynonymId);
        var reopened = Assert.Single(rows, row => row.Id == second.UnresolvedCityId);
        Assert.Equal(UnresolvedCityStatus.Pending, reopened.Status);
        Assert.Null(reopened.SynonymId);
        Assert.Equal(second.UnresolvedCityId, Assert.Single(
            await OccurrencesAsync(readContext), occurrence => occurrence.SeenAt == Seen.AddHours(1)).UnresolvedCityId);
    }

    // El recorrido histórico (SC-13) reporta pedidos viejos después de los nuevos. La hora llega con el desfase de la
    // tienda (-05:00): se guarda en UTC.
    [Fact]
    public async Task AnOlderSeenAtDoesNotMoveLastSeenBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);
        var bogota = TimeSpan.FromHours(-5);
        var newer = new DateTimeOffset(2026, 10, 20, 10, 0, 0, bogota);
        var older = newer.AddDays(-3);
        await ReportAsync(factory, Report(department, "Calli") with
        {
            SampleAddress = "La nueva",
            ProvinceCode = "VAC",
            SeenAt = newer,
        });

        var result = await ReportAsync(factory, Report(department, "Calli") with
        {
            SampleAddress = "La vieja",
            ProvinceCode = null,
            SeenAt = older,
        });

        Assert.Equal(UnresolvedCityReportOutcome.Recorded, result.Outcome);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = Assert.Single(await RowsAsync(dbContext));
        Assert.Equal(newer, row.LastSeenAt);
        Assert.Equal(older, row.FirstSeenAt);
        Assert.Equal("La nueva", row.SampleAddress);
        Assert.Equal("VAC", row.ProvinceCode);
        Assert.Equal(2, (await OccurrencesAsync(dbContext)).Count);
    }

    // Un texto que se normaliza a vacío no es una ciudad que un operador pueda resolver: no abre fila ni ocurrencia, y
    // tampoco lanza, porque una importación que lanza se reintenta para siempre.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ABlankCityTextIsIgnoredWithoutWriting(string cityText)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        var department = await ValleAsync(factory);

        var result = await ReportAsync(factory, Report(department, cityText));

        Assert.Equal(new UnresolvedCityReportResult(UnresolvedCityReportOutcome.Ignored, null, null, null), result);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.Empty(await RowsAsync(dbContext));
        Assert.Empty(await OccurrencesAsync(dbContext));
    }

    private static UnresolvedCityReport Report(DepartmentId department, string cityText) =>
        new(
            ReportTenantId,
            "shopify",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            department,
            cityText,
            "Calle 5 # 10-20, Barrio San Fernando",
            null,
            false,
            false,
            Seen);

    // Un scope por reporte, como cada entrega del webhook: dos reportes a la vez van por dos conexiones.
    private static async Task<UnresolvedCityReportResult> ReportAsync(QepApiFactory factory, UnresolvedCityReport report)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnresolvedCityReporter>()
            .ReportAsync(report, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAdvisoryLockWaitersAsync(GeographyDbContext dbContext, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var waiting = await dbContext.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted")
                .SingleAsync(TestContext.Current.CancellationToken);
            if (waiting == expected)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {expected} reports waiting on the lock, saw {waiting}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<DepartmentId> ValleAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await dbContext.Departments.SingleAsync(
            value => value.DivipolaCode == "76", TestContext.Current.CancellationToken);
        return department.Id;
    }

    private static async Task<(CitySynonymId SynonymId, CityId CityId)> AddSynonymAsync(
        QepApiFactory factory, DepartmentId department, string text)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var cali = await dbContext.Cities.SingleAsync(
            value => value.DivipolaCode == "76001", TestContext.Current.CancellationToken);
        var synonym = CitySynonym.Create(CitySynonymId.New(), department, text, cali.Id, OperatorId, Seen);
        scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>().Add(synonym);
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return (synonym.Id, cali.Id);
    }

    private static Task<List<UnresolvedCity>> RowsAsync(GeographyDbContext dbContext) =>
        dbContext.UnresolvedCities.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

    private static Task<List<UnresolvedCityOccurrence>> OccurrencesAsync(GeographyDbContext dbContext) =>
        dbContext.UnresolvedCityOccurrences.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
}
