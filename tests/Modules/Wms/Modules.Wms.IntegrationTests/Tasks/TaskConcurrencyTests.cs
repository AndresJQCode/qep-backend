using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Dos peticiones a la vez sobre el motor (spec 2026-11-26 §6.8): el lock del operario, <c>SKIP LOCKED</c> y el
/// <c>FOR UPDATE</c> de la tarea.</summary>
public sealed class TaskConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskDto> NextAsync(HttpClient client, Guid warehouseId)
    {
        var response = await client.PostAsJsonAsync(
            $"{TasksUrl()}/next", new { warehouseId, deviceId = "zebra-01" }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<TaskDto>(response);
    }

    [Fact]
    public async Task TwoOperatorsAskingAtOnceGetDifferentTasks()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        var (_, first) = await SeedOperatorAsync(factory);
        var (_, second) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        using var _f = first;
        using var _s = second;

        var results = await Task.WhenAll(NextAsync(first, s.WarehouseId), NextAsync(second, s.WarehouseId));

        Assert.NotEqual(results[0].Id, results[1].Id);
    }

    [Fact]
    public async Task TheSameOperatorTappingTwiceGetsOneTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var results = await Task.WhenAll(NextAsync(client, s.WarehouseId), NextAsync(client, s.WarehouseId));

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks WHERE status = 'InProgress'"));
    }

    [Fact]
    public async Task TwoConfirmationsOfTheSameTaskSerialize()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        await CreateAsync(
            s.Manager,
            Body(s, [Line(s.VariantId, s.BinA, destination, 3), Line(s.VariantId, s.BinA, destination, 4)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await NextAsync(client, s.WarehouseId);

        var responses = await Task.WhenAll(task.Lines.Select(line =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{TasksUrl()}/{task.Id}/lines/{line.Id}/confirm")
            {
                Content = JsonContent.Create(new
                {
                    leaseToken = task.LeaseToken,
                    fromLocationId = line.FromLocationId,
                    toLocationId = line.ToLocationId,
                    quantity = line.QuantityPlanned,
                    occurredAt = DateTimeOffset.UtcNow,
                    deviceId = "zebra-01",
                }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return client.SendAsync(request, Ct);
        }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var done = await GetAsync(s.Manager, task.Id);
        // Crear 1, reclamar 2 y las dos confirmaciones.
        Assert.Equal(("Done", 4L), (done.Status, done.Version));
        Assert.Equal(3m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(7m, await OnHandAsync(database, destination, s.VariantId, StockStatus.Available));
    }
}
