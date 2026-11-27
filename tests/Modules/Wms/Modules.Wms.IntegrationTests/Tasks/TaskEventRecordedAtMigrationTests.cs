using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Wms.Infrastructure.Persistence;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>La migración <c>AddTaskEventRecordedAt</c> (revisión T1, B3): llena <c>recorded_at</c> con la hora del hecho y deja
/// insertar a un pod viejo que no conoce la columna. Corre sobre su propio host porque baja y vuelve a subir la migración.</summary>
public sealed class TaskEventRecordedAtMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMigrationBackfillsRecordedAtAndAnOldPodCanStillInsert()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(database.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema))
            .Options;
        await using var context = new WmsDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var before = context.Database.GetMigrations().Single(m => m.EndsWith("_AddTaskBoardIndexes", StringComparison.Ordinal));

        await migrator.MigrateAsync(before, Ct);
        await migrator.MigrateAsync(null, Ct);

        Assert.True(await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE task_id = @id", ("id", task.Id)) > 0);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.task_events WHERE recorded_at <> occurred_at"));
        // Un pod viejo no conoce la columna: inserta sin ella y cae en el default.
        var oldPodEventId = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            "INSERT INTO wms.task_events (id, tenant_id, task_id, kind, occurred_at) VALUES (@id, @tenant, @task, 'PriorityChanged', now())",
            ("id", oldPodEventId), ("tenant", s.TenantId), ("task", task.Id));
        // El default es la hora de la base: la columna es NOT NULL, así que no basta con que no sea nula.
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                database,
                "SELECT count(*) FROM wms.task_events WHERE id = @id AND abs(extract(epoch FROM (recorded_at - now()))) < 60",
                ("id", oldPodEventId)));
    }
}
