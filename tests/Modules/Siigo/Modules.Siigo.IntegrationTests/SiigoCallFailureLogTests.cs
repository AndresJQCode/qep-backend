using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-21: el registro de las llamadas a Siigo que fallan. Contra una base real y migrada, sin levantar el host: el registro
/// sólo necesita las opciones del <c>DbContext</c>, el contexto de ejecución y el reloj.
/// </summary>
public sealed class SiigoCallFailureLogTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a01");
    private static readonly Guid OtherTenantId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a02");

    private sealed class FixedExecutionContext(Guid tenantId) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a99");

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => true;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class CapturingLogger : ILogger<SiigoCallFailureLog>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private static SiigoCallFailureReport Report(string curl = "curl.exe -X POST \"https://api.siigo.com/v1/invoices\"") =>
        new(
            Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a10"),
            SiigoCallOperation.InvoiceCreate,
            "POST",
            "v1/invoices",
            400,
            "http_400",
            "abc123",
            null,
            curl,
            """{"Errors":[{"Code":"invalid_total_payments"}]}""");

    private static SiigoCallFailureLog NewLog(string connectionString, Guid tenantId, ILogger<SiigoCallFailureLog>? logger = null) =>
        new(
            new DbContextOptionsBuilder<SiigoDbContext>().UseNpgsql(connectionString).Options,
            new FixedExecutionContext(tenantId),
            new FixedClock(DateTimeOffset.UtcNow),
            logger ?? new CapturingLogger());

    private static Task SeedAsync(string connectionString, Guid tenantId, int count, int daysOld) =>
        SiigoApiHarness.ExecuteAsync(
            connectionString,
            """
            INSERT INTO siigo.call_failures (id, tenant_id, connection_id, operation, method, path, reason, request_curl, occurred_at)
            SELECT gen_random_uuid(), @tenant, @tenant, 'ListRead', 'GET', 'v1/x', 'http_500', 'curl',
                   now() - make_interval(days => @days) - make_interval(secs => g)
            FROM generate_series(1, @count) g
            """,
            ("tenant", tenantId),
            ("days", daysOld),
            ("count", count));

    private static Task<long> CountAsync(string connectionString, Guid? tenantId = null) =>
        tenantId is { } id
            ? SiigoApiHarness.ScalarAsync<long>(connectionString, "SELECT count(*) FROM siigo.call_failures WHERE tenant_id = @tenant", ("tenant", id))
            : SiigoApiHarness.ScalarAsync<long>(connectionString, "SELECT count(*) FROM siigo.call_failures");

    [Fact]
    public async Task RecordWritesTheRowWithTheTenantOfTheContext()
    {
        await using var database = await SiigoApiHarness.StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        await NewLog(connectionString, TenantId).RecordAsync(Report(), TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountAsync(connectionString, TenantId));
        Assert.Equal(
            "InvoiceCreate",
            await SiigoApiHarness.ScalarAsync<string>(connectionString, "SELECT operation FROM siigo.call_failures WHERE tenant_id = @tenant", ("tenant", TenantId)));
    }

    [Fact]
    public async Task RecordDeletesWhatIsOlderThanSevenDaysAndKeepsWhatIsNewer()
    {
        await using var database = await SiigoApiHarness.StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await SeedAsync(connectionString, TenantId, count: 3, daysOld: 8);
        await SeedAsync(connectionString, OtherTenantId, count: 2, daysOld: 9);
        await SeedAsync(connectionString, TenantId, count: 4, daysOld: 6);

        await NewLog(connectionString, TenantId).RecordAsync(Report(), TestContext.Current.CancellationToken);

        // Las 5 vencidas (de los dos tenants: el log es operativo) se fueron; quedan las 4 recientes y la nueva.
        Assert.Equal(5, await CountAsync(connectionString));
        Assert.Equal(0, await SiigoApiHarness.ScalarAsync<long>(connectionString, "SELECT count(*) FROM siigo.call_failures WHERE occurred_at < now() - interval '7 days'"));
    }

    [Fact]
    public async Task RecordDeletesAtMostTwoHundredExpiredRowsPerWrite()
    {
        await using var database = await SiigoApiHarness.StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await SeedAsync(connectionString, TenantId, count: 250, daysOld: 8);

        await NewLog(connectionString, TenantId).RecordAsync(Report(), TestContext.Current.CancellationToken);

        Assert.Equal(51, await CountAsync(connectionString));
    }

    [Fact]
    public async Task RecordSwallowsItsOwnFailureAndLogsAnError()
    {
        var logger = new CapturingLogger();
        var log = NewLog("Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=x;Timeout=2;Command Timeout=2", TenantId, logger);

        await log.RecordAsync(Report(), TestContext.Current.CancellationToken);

        Assert.Contains(LogLevel.Error, logger.Levels);
    }
}
