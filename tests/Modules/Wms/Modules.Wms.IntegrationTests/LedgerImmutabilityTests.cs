using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Infrastructure.Persistence;
using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>El ledger es prueba de negocio (RNF-01): la base rechaza UPDATE y DELETE con un trigger, no la aplicación.</summary>
public sealed class LedgerImmutabilityTests
{
    [Fact]
    public async Task UpdatingAMovementFailsWithTheImmutableCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var id = await InsertRawMovementAsync(database, anchors, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "UPDATE wms.stock_movements SET quantity = 1 WHERE id = @id", ("id", id)));

        Assert.Equal("wms.ledger.immutable", exception.MessageText);
    }

    [Fact]
    public async Task DeletingAMovementFailsWithTheImmutableCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var id = await InsertRawMovementAsync(database, anchors, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "DELETE FROM wms.stock_movements WHERE id = @id", ("id", id)));

        Assert.Equal("wms.ledger.immutable", exception.MessageText);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
    }

    // El Down está vacío a propósito (S11): bajar a InitialWms deja la tabla, la fila y el trigger. La prueba termina abajo
    // y no llama a la API después (el Up no se puede volver a aplicar sobre las tablas que EF creó).
    [Fact]
    public async Task TheDownMigrationKeepsTheTriggerAndTheRows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var id = await InsertRawMovementAsync(database, anchors, DateTimeOffset.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var migrator = context.GetService<IMigrator>();
        var initialWms = context.Database.GetMigrations().Single(m => m.EndsWith("_InitialWms", StringComparison.Ordinal));

        await migrator.MigrateAsync(initialWms, TestContext.Current.CancellationToken);

        // Sólo el de la tabla padre: PostgreSQL clona el trigger a cada partición, así que contar por nombre da uno por partición.
        Assert.Equal(1L, await ScalarAsync<long>(
            database,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_stock_movements_immutable' AND tgrelid = 'wms.stock_movements'::regclass"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
        Assert.NotNull(await ScalarAsync<string?>(database, "SELECT to_regclass('wms.stock_balances')::text"));
        Assert.NotNull(await ScalarAsync<string?>(database, "SELECT to_regclass('wms.v_stock_summary')::text"));
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "DELETE FROM wms.stock_movements WHERE id = @id", ("id", id)));
        Assert.Equal("wms.ledger.immutable", exception.MessageText);
    }
}
