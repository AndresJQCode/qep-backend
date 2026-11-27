using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Workers;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>La acción de una línea que llega con el lease perdido se rechaza con 409, pero queda como novedad <c>SyncConflict</c>
/// (spec 2026-11-26 §6.7 y DA-48): es un hecho físico que el supervisor tiene que poder ver.</summary>
public sealed class TaskSyncConflictApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Floor(LedgerSetup Setup, Guid Destination, TaskDto Task, Guid WorkerA, HttpClient ClientA, Guid WorkerB);

    /// <summary>El operario A reclamó una tarea de 5; el supervisor se la reasigna al B: el token de A ya no vale.</summary>
    private static async Task<Floor> ArrangeReassignedAsync(PostgreSqlContainer database, QepApiFactory factory)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (workerA, clientA) = await SeedOperatorAsync(factory);
        var (workerB, clientB) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        clientB.Dispose();
        var claimed = await ReadAsync<TaskDto>(
            await clientA.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));
        var current = await GetAsync(s.Manager, claimed.Id);
        await ActionAsync(
            s.Manager, claimed.Id, "assign", new { memberId = workerB, version = current.Version, reason = "Cambio de turno" });
        return new Floor(s, destination, claimed, workerA, clientA, workerB);
    }

    private static async Task<HttpResponseMessage> ConfirmRawAsync(
        HttpClient client, Floor floor, string? key, decimal quantity = 2, DateTimeOffset? occurredAt = null, Guid? taskId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{TasksUrl()}/{taskId ?? floor.Task.Id}/lines/{floor.Task.Lines[0].Id}/confirm")
        {
            Content = JsonContent.Create(new
            {
                leaseToken = floor.Task.LeaseToken,
                fromLocationId = floor.Task.Lines[0].FromLocationId,
                toLocationId = floor.Task.Lines[0].ToLocationId,
                quantity,
                occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<long> ReceiptsOfAsync(PostgreSqlContainer database, string key) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts WHERE idempotency_key = @key", ("key", key));

    [Fact]
    public async Task AConfirmationAfterReassignmentLeavesASyncConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        var before = await GetAsync(floor.Setup.Manager, floor.Task.Id);
        var onHand = await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available);
        var key = Guid.NewGuid().ToString();

        var response = await ConfirmRawAsync(floor.ClientA, floor, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        Assert.False(await ResumableOfAsync(response));
        var conflict = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        Assert.Equal(("SyncConflict", "Open", floor.WorkerA, key), (conflict.Type, conflict.Status, conflict.ReportedBy, conflict.OperationKey));
        Assert.Equal((floor.Task.Lines[0].Id, 2m), (conflict.SourceTaskLineId, conflict.ActualQty));
        var payload = JsonNode.Parse(conflict.RawPayload!)!.AsObject();
        Assert.Equal("confirm", payload["action"]!.GetValue<string>());
        Assert.Equal(2m, payload["quantity"]!.GetValue<decimal>());
        Assert.False(payload.ContainsKey("leaseToken"));
        var after = await GetAsync(floor.Setup.Manager, floor.Task.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Lines[0].QuantityConfirmed, after.Lines[0].QuantityConfirmed);
        Assert.Equal(onHand, await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available));
        Assert.Equal(0L, await ReceiptsOfAsync(database, key));
    }

    [Fact]
    public async Task RetryingTheSameKeyDoesNotDuplicateTheConflictAndStill409s()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        var key = Guid.NewGuid().ToString();

        var first = await ConfirmRawAsync(floor.ClientA, floor, key);
        var second = await ConfirmRawAsync(floor.ClientA, floor, key);

        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False(await ResumableOfAsync(first));
        Assert.False(await ResumableOfAsync(second));
        Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
    }

    [Fact]
    public async Task ADifferentKeyLeavesASecondConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;

        await ConfirmRawAsync(floor.ClientA, floor, Guid.NewGuid().ToString());
        await ConfirmRawAsync(floor.ClientA, floor, Guid.NewGuid().ToString());

        Assert.Equal(2, (await IncidentsOfTaskAsync(database, floor.Task.Id)).Count);
    }

    [Fact]
    public async Task TheSameKeyWithAnotherBodyKeepsTheFirstPayload()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        var key = Guid.NewGuid().ToString();

        await ConfirmRawAsync(floor.ClientA, floor, key, quantity: 2);
        var second = await ConfirmRawAsync(floor.ClientA, floor, key, quantity: 3);

        // Sin recibo no hay wms.idempotency_key_reused: sigue siendo el 409 del lease.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(second));
        var conflict = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        Assert.Equal(2m, conflict.ActualQty);
    }

    [Fact]
    public async Task ACountWithALostLeaseLeavesAConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await TaskCountApiTests.ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (counterA, clientA) = await SeedCounterAsync(factory);
        using var _a = clientA;
        var (counterB, clientB) = await SeedMemberAsync(
            factory, "01900000-0000-7000-8000-000000000005", WmsPermissions.DocumentRead, WmsPermissions.CountPerform);
        clientB.Dispose();
        var count = await ReadAsync<TaskDto>(await NextRawAsync(clientA, shortage.Setup.WarehouseId));
        var current = await GetAsync(shortage.Setup.Manager, count.Id);
        await ActionAsync(
            shortage.Setup.Manager, count.Id, "assign", new { memberId = counterB, version = current.Version, reason = "Cambio de turno" });
        var key = Guid.NewGuid().ToString();

        var response = await CountRawAsync(
            clientA, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, shortage.Setup.BinA, 5), key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        var conflict = Assert.Single(await IncidentsOfTaskAsync(database, count.Id));
        Assert.Equal(("SyncConflict", "Open", 5m, counterA, key), (conflict.Type, conflict.Status, conflict.ActualQty, conflict.ReportedBy, conflict.OperationKey));
        var payload = JsonNode.Parse(conflict.RawPayload!)!.AsObject();
        Assert.Equal("count", payload["action"]!.GetValue<string>());
        Assert.Equal(5m, payload["quantityCounted"]!.GetValue<decimal>());
        Assert.False(payload.ContainsKey("leaseToken"));
        // Lo rechazado no se aplica: ni conteo, ni ajuste.
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Null((await GetAsync(shortage.Setup.Manager, count.Id)).Lines[0].QuantityCounted);
    }

    [Fact]
    public async Task AShortWithALostLeaseAlsoLeavesAConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;

        var response = await ShortRawAsync(
            floor.ClientA,
            floor.Task.Id,
            floor.Task.Lines[0].Id,
            ShortBody(floor.Task.LeaseToken!.Value, 3),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        Assert.Equal(("SyncConflict", 3m), (conflict.Type, conflict.ActualQty));
        var payload = JsonNode.Parse(conflict.RawPayload!)!.AsObject();
        Assert.Equal("short", payload["action"]!.GetValue<string>());
        Assert.Equal(3m, payload["quantityFound"]!.GetValue<decimal>());
        Assert.False(payload.ContainsKey("leaseToken"));
    }

    [Fact]
    public async Task AResumableLateConfirmationLeavesNoConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        Assert.Equal(1, await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct));
        var floor = new Floor(s, destination, task, Guid.Empty, client, Guid.Empty);

        var response = await ConfirmRawAsync(client, floor, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await IncidentsOfTaskAsync(database, task.Id));
    }

    [Fact]
    public async Task AConfirmationOnAHeldTaskLeavesAConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));
        // Sólo una tarea que volvió al pool se retiene: el barrido vence el lease y la retención borra el reclamo que dejó (DA-35).
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        Assert.Equal(1, await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct));
        var held = await ActionAsync(s.Manager, task.Id, "hold", new { version = (await GetAsync(s.Manager, task.Id)).Version, reason = "pausa" });
        await ActionAsync(s.Manager, task.Id, "publish", new { version = held.Version });
        var floor = new Floor(s, destination, task, Guid.Empty, client, Guid.Empty);

        var response = await ConfirmRawAsync(client, floor, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await ResumableOfAsync(response));
        Assert.Equal("SyncConflict", Assert.Single(await IncidentsOfTaskAsync(database, task.Id)).Type);
    }

    [Fact]
    public async Task WithoutTheTypePermissionItIs403AndLeavesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        using var reader = CreateClient(factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead);

        var response = await ConfirmRawAsync(reader, floor, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await IncidentsOfTaskAsync(database, floor.Task.Id));
    }

    [Fact]
    public async Task AnUnknownTaskIs404AndLeavesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;

        var response = await ConfirmRawAsync(floor.ClientA, floor, Guid.NewGuid().ToString(), taskId: Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.incidents"));
    }

    [Fact]
    public async Task AnAncientScanIsReportedAtServerTime()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        var ancient = DateTimeOffset.UtcNow.AddDays(-40);

        var response = await ConfirmRawAsync(floor.ClientA, floor, Guid.NewGuid().ToString(), occurredAt: ancient);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        Assert.Equal(DateTimeOffset.UtcNow, conflict.ReportedAt, TimeSpan.FromMinutes(2));
        var payload = JsonNode.Parse(conflict.RawPayload!)!.AsObject();
        Assert.Equal(ancient, payload["occurredAt"]!.GetValue<DateTimeOffset>(), TimeSpan.FromSeconds(1));
    }

    // Sólo quien tuvo el reclamo deja novedad: B nunca reclamó esta tarea.
    [Fact]
    public async Task AMemberWhoNeverClaimedTheTaskLeavesNoConflictEvenWithManyKeys()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeReassignedAsync(database, factory);
        using var _c = floor.ClientA;
        using var stranger = CreateClient(
            factory, SecondOperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform);
        var forged = floor with { Task = floor.Task with { LeaseToken = Guid.NewGuid() } };

        foreach (var _k in Enumerable.Range(0, 3))
        {
            var response = await ConfirmRawAsync(stranger, forged, Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
            Assert.False(await ResumableOfAsync(response));
        }

        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.incidents"));
    }
}
