using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Workers;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>La limpieza diaria contra el esquema real. El temporizador no corre en las pruebas (el harness fija 24 h): cada prueba
/// llama <c>RunOnceAsync</c> a mano.</summary>
public sealed class WmsHousekeepingTests
{
    private static WmsHousekeepingWorker WorkerOf(QepApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<WmsHousekeepingWorker>().Single();

    private static Task<int> InsertReceiptAsync(PostgreSqlContainer database, string key, DateTimeOffset createdAt) =>
        ExecuteAsync(
            database,
            """
            INSERT INTO wms.operation_receipts (tenant_id, idempotency_key, request_hash, response_json, created_at)
            VALUES (@tenant, @key, @hash, '{}'::jsonb, @createdAt)
            """,
            ("tenant", Guid.Parse(TenantId)),
            ("key", key),
            ("hash", new string('a', 64)),
            ("createdAt", createdAt));

    [Fact]
    public async Task ReceiptsOlderThanTheRetentionAreDeletedAndRecentOnesKept()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await InsertReceiptAsync(database, "old", DateTimeOffset.UtcNow.AddDays(-46));
        await InsertReceiptAsync(database, "recent", DateTimeOffset.UtcNow.AddDays(-44));

        var result = await WorkerOf(factory).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Receipts);
        Assert.Equal(
            ["recent"],
            [.. await ReadKeysAsync(database)]);
    }

    [Fact]
    public async Task TheFirstPassRunsSoonAfterStartWithoutWaitingTheInterval()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await InsertReceiptAsync(database, "old", DateTimeOffset.UtcNow.AddDays(-46));
        // Un worker propio con espera de arranque 0 y un intervalo de 24 h: si sólo corriera al cumplirse el intervalo, el recibo seguiría ahí.
        using var worker = new WmsHousekeepingWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WmsWorkerOptions { HousekeepingStartupSeconds = 0, HousekeepingHours = 24 }),
            NullLogger<WmsHousekeepingWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline
            && await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts") > 0)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        await worker.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
    }

    [Fact]
    public async Task EmptyBalancesArePrunedAndOccupiedOnesKept()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 5);
        // Devolverlo todo deja la fila del bin en cero; las del proveedor (0 tras volver) también.
        await PostAsync(factory, Batch(s, Entry(s, s.BinA, StockStatus.Available, s.Supplier, StockStatus.Available, 5)));
        await StockAsync(factory, s, s.Receiving, 3);

        var result = await WorkerOf(factory).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Balances >= 1);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_balances WHERE on_hand = 0 AND reserved = 0"));
        Assert.Equal(3m, await OnHandAsync(database, s.Receiving, s.VariantId, StockStatus.Available));
    }

    private static async Task<List<string>> ReadKeysAsync(PostgreSqlContainer database)
    {
        var count = await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts");
        var keys = new List<string>();
        for (var index = 0; index < count; index++)
        {
            keys.Add(await ScalarAsync<string>(
                database,
                "SELECT idempotency_key FROM wms.operation_receipts ORDER BY created_at DESC OFFSET @o LIMIT 1",
                ("o", index)));
        }

        return keys;
    }
}
