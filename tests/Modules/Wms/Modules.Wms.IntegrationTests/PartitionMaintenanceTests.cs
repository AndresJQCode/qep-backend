using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Workers;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

public sealed class PartitionMaintenanceTests
{
    private static DateOnly NextMonth() => DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1);

    private static string PartitionOf(DateOnly month) => $"wms.stock_movements_y{month:yyyy}m{month:MM}";

    private static async Task<bool> ExistsAsync(QepApiFactory factory, DateOnly month)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPartitionMaintenance>().ExistsAsync(month, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheGaugeReportsOneWhileNextMonthHasNoPartition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var next = NextMonth();
        await ExecuteAsync(database, $"DROP TABLE {PartitionOf(next)}");
        Assert.False(await ExistsAsync(factory, next));
        // Una fila del mes siguiente cae en DEFAULT: crear la partición ahora viola su restricción y el worker no puede repararlo solo.
        await InsertRawMovementAsync(database, anchors, new DateTimeOffset(next.Year, next.Month, 15, 12, 0, 0, TimeSpan.Zero));
        var worker = factory.Services.GetRequiredService<PartitionMaintenanceWorker>();

        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(await ExistsAsync(factory, next));
        Assert.Equal(1L, WmsGaugeReader.Read(factory, "qep.wms.partition.missing"));
    }

    // ADR-10: el riesgo es que falte la partición del mes. Si el gauge sólo mirara el siguiente, se apagaría el día que el mes sin
    // partición pasa a ser el actual, justo cuando los movimientos empiezan a caer en DEFAULT.
    [Fact]
    public async Task TheGaugeReportsOneWhileTheCurrentMonthHasNoPartitionEvenIfNextExists()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        var current = DateOnly.FromDateTime(DateTime.UtcNow);
        await ExecuteAsync(database, $"DROP TABLE {PartitionOf(current)}");
        await InsertRawMovementAsync(database, anchors, new DateTimeOffset(current.Year, current.Month, 1, 0, 30, 0, TimeSpan.Zero));
        var worker = factory.Services.GetRequiredService<PartitionMaintenanceWorker>();

        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(await ExistsAsync(factory, NextMonth()));
        Assert.False(await ExistsAsync(factory, current));
        Assert.Equal(1L, WmsGaugeReader.Read(factory, "qep.wms.partition.missing"));
    }

    [Fact]
    public async Task RunOnceIsIdempotentAndRecreatesAMissingPartitionWhenDefaultIsClean()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var next = NextMonth();
        await ExecuteAsync(database, $"DROP TABLE {PartitionOf(next)}");
        var worker = factory.Services.GetRequiredService<PartitionMaintenanceWorker>();

        await worker.RunOnceAsync(TestContext.Current.CancellationToken);
        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(await ExistsAsync(factory, next));
        Assert.Equal(0L, WmsGaugeReader.Read(factory, "qep.wms.partition.missing"));
    }

    [Fact]
    public void ThePartitionMaintenanceExposesNoDetach()
    {
        var methods = typeof(IPartitionMaintenance).GetMethods().Select(method => method.Name).Order().ToArray();

        Assert.Equal(["EnsureAsync", "ExistsAsync"], methods);
    }
}
