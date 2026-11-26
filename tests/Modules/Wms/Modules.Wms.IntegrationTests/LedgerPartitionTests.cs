using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

public sealed class LedgerPartitionTests
{
    [Fact]
    public async Task TheMigrationCreatesTheDefaultTheCurrentAndTheNextMonthPartitions()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        // Los nombres salen del mes UTC: la migración calcula con now() en UTC (S26).
        var now = DateTimeOffset.UtcNow;
        var next = now.AddMonths(1);

        var partitions = await ScalarAsync<string[]>(
            database,
            "SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'stock_movements'");

        Assert.Contains("stock_movements_default", partitions);
        Assert.Contains($"stock_movements_y{now:yyyy}m{now:MM}", partitions);
        Assert.Contains($"stock_movements_y{next:yyyy}m{next:MM}", partitions);
    }

    [Fact]
    public async Task AMovementOutsideEveryMonthlyPartitionLandsInTheDefaultPartition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var farFuture = DateTimeOffset.UtcNow.AddMonths(6);

        var id = await InsertRawMovementAsync(database, anchors, farFuture);

        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements_default WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
    }

    // Los límites están en UTC (S26): una fila a las 23:30 UTC del último día del mes cae en ese mes, no en el siguiente.
    [Fact]
    public async Task PartitionBoundsAreUtc()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var now = DateTimeOffset.UtcNow;
        var lastInstantOfMonth = new DateTimeOffset(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 30, 0, TimeSpan.Zero);

        var id = await InsertRawMovementAsync(database, anchors, lastInstantOfMonth);

        Assert.Equal(1L, await ScalarAsync<long>(database, $"SELECT count(*) FROM wms.stock_movements_y{now:yyyy}m{now:MM} WHERE id = @id", ("id", id)));
    }
}
