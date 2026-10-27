using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Infrastructure.Persistence;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-25 D-E5 y D-E7 contra Postgres: la tabla <c>automations.run_units</c>, su unicidad por entrante y la purga
/// de 3 días en el barrido.</summary>
public sealed class RunUnitsPersistenceTests
{
    private const string StepsJson = """[{"step":1,"seq":0,"nodeId":"msg1","kind":"Message","outcome":"ok","writes":{"name":"Ana"}}]""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunUnitEntry Unit(
        Guid tenantId, Guid runId, Guid? inboundMessageId, DateTimeOffset startedAt, int baseStep = 0, string steps = StepsJson) =>
        new(tenantId, runId, baseStep, RunUnitTriggers.CustomerMessage, inboundMessageId, "advanced", startedAt, startedAt.AddSeconds(1),
            steps, null, null);

    private static async Task SaveAsync(QepApiFactory factory, params RunUnitEntry[] entries)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRunUnitRepository>();
        foreach (var entry in entries)
        {
            repository.Add(entry);
        }

        await scope.ServiceProvider.GetRequiredService<AutomationsDbContext>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AUnitRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
        var closed = new RunUnitEntry(tenantId, runId, 3, RunUnitTriggers.Closed, null, "interrupted", at, at, "[]", "Interrupted",
            RunCloseReasons.HumanTookOver);
        var message = Unit(tenantId, runId, Guid.CreateVersion7(), at, baseStep: 0);
        await SaveAsync(factory, closed, message);

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRunUnitRepository>();
        var listed = await repository.ListAsync(tenantId, runId, Ct);
        var fromAnotherTenant = await repository.ListAsync(Guid.CreateVersion7(), runId, Ct);

        // Ordena por (base_step, started_at, id): la de base 0 antes que el cierre de base 3.
        Assert.Equal(2, listed.Count);
        Assert.Equal(message with { StepsJson = listed[0].StepsJson }, listed[0]);
        Assert.Equal(closed, listed[1]);
        Assert.Empty(fromAnotherTenant);
        // jsonb normaliza el espacio, no el contenido.
        using var steps = System.Text.Json.JsonDocument.Parse(listed[0].StepsJson);
        Assert.Equal("Ana", steps.RootElement[0].GetProperty("writes").GetProperty("name").GetString());
        Assert.Equal("jsonb", await ScalarAsync<string>(database.GetConnectionString(),
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'automations' AND table_name = 'run_units' AND column_name = 'steps'"));
    }

    [Fact]
    public async Task TheSameInboundTwiceInOneRunIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var inbound = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() =>
            SaveAsync(factory, Unit(tenantId, runId, inbound, at), Unit(tenantId, runId, inbound, at.AddSeconds(5), baseStep: 4)));
        Assert.Contains(AutomationsDbContext.RunUnitInboundIndex, duplicate.InnerException?.Message);

        // Sin entrante (cierres del barrido) pasan todos; el mismo entrante en otra corrida también.
        await SaveAsync(factory, Unit(tenantId, runId, null, at), Unit(tenantId, runId, null, at.AddSeconds(5), baseStep: 4));
        await SaveAsync(factory, Unit(tenantId, Guid.CreateVersion7(), inbound, at));
        Assert.Equal(3L, await CountAsync(database.GetConnectionString(), "SELECT count(*) FROM automations.run_units"));
    }

    [Fact]
    public async Task ThePurgeDeletesByBatchOnlyWhatIsOlder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await SaveAsync(
            factory,
            Unit(tenantId, runId, Guid.CreateVersion7(), now.AddDays(-5), baseStep: 0),
            Unit(tenantId, runId, Guid.CreateVersion7(), now.AddDays(-4), baseStep: 1),
            Unit(tenantId, runId, Guid.CreateVersion7(), now.AddDays(-4), baseStep: 2),
            Unit(tenantId, runId, Guid.CreateVersion7(), now.AddDays(-1), baseStep: 3));
        var cutoff = now.AddDays(-3);

        async Task<int> PurgeAsync()
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IRunUnitRepository>().PurgeBeforeAsync(cutoff, 2, Ct);
        }

        Assert.Equal(2, await PurgeAsync());
        Assert.Equal(1, await PurgeAsync());
        Assert.Equal(0, await PurgeAsync());
        Assert.Equal(3, await ScalarAsync<int>(database.GetConnectionString(), "SELECT base_step FROM automations.run_units"));
    }

    [Fact]
    public async Task TheSweepPurgesRunUnitsAfterTheRetention()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.CreateVersion7();
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new RunState(RunStatus.Waiting, null, 0, 0, RunVariables.Empty(), []), now.AddDays(-4));
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConversationRunRepository>().AddAsync(run, Ct);
            await scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await SaveAsync(
            factory,
            Unit(tenantId, run.Id, Guid.CreateVersion7(), now.AddDays(-4), baseStep: 0),
            Unit(tenantId, run.Id, Guid.CreateVersion7(), now.AddDays(-2), baseStep: 5));
        var traceBefore = await ScalarAsync<string>(connectionString, "SELECT trace::text FROM automations.conversation_runs");

        await SweepAutomationsAsync(factory, now);

        Assert.Equal(5, await ScalarAsync<int>(connectionString, "SELECT base_step FROM automations.run_units"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM automations.conversation_runs"));
        Assert.Equal(traceBefore, await ScalarAsync<string>(connectionString, "SELECT trace::text FROM automations.conversation_runs"));
    }
}
