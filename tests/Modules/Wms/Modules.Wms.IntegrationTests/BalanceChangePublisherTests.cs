using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Workers;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

public sealed class BalanceChangePublisherTests
{
    private const string EventCount = "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'wms.balance.changed.v1'";

    [Fact]
    public async Task ThreeMovementsOfTheSameVariantProduceOneEvent()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 4);
        await StockAsync(factory, s, s.BinA, 3);
        await StockAsync(factory, s, s.BinA, 2);
        var publisher = factory.Services.GetRequiredService<BalanceChangePublisher>();

        var published = await publisher.PublishOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, published);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_change_queue"));
        Assert.Equal(1L, await ScalarAsync<long>(database, EventCount));
        using var payload = JsonDocument.Parse(
            await ScalarAsync<string>(database, "SELECT payload::text FROM platform.outbox_messages WHERE event_name = 'wms.balance.changed.v1'"));
        var body = payload.RootElement;
        Assert.Equal(9m, body.GetProperty("available").GetDecimal());
        Assert.Equal(9m, body.GetProperty("onHand").GetDecimal());
        Assert.Equal(0m, body.GetProperty("reserved").GetDecimal());
        Assert.Equal(s.TenantId, body.GetProperty("tenantId").GetGuid());
        Assert.Equal(s.OwnerId, body.GetProperty("ownerId").GetGuid());
        Assert.Equal(s.WarehouseId, body.GetProperty("warehouseId").GetGuid());
        Assert.Equal(s.VariantId, body.GetProperty("variantId").GetGuid());
        Assert.True(body.TryGetProperty("changedAt", out var changedAt) && changedAt.ValueKind == JsonValueKind.String);

        Assert.Equal(0, await publisher.PublishOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, await ScalarAsync<long>(database, EventCount));
    }

    [Fact]
    public async Task AKeyChangedAgainAfterPublishingIsPublishedAgainWithTheNewBalance()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 5);
        var publisher = factory.Services.GetRequiredService<BalanceChangePublisher>();
        await publisher.PublishOnceAsync(TestContext.Current.CancellationToken);
        await PostAsync(factory, Batch(s, Entry(s, s.BinA, StockStatus.Available, s.Supplier, StockStatus.Available, 2)));

        Assert.Equal(1, await publisher.PublishOnceAsync(TestContext.Current.CancellationToken));

        var available = await ScalarAsync<decimal>(
            database,
            "SELECT (payload->>'available')::numeric FROM platform.outbox_messages WHERE event_name = 'wms.balance.changed.v1' ORDER BY occurred_at DESC, id DESC LIMIT 1");
        Assert.Equal(3m, available);
    }
}
