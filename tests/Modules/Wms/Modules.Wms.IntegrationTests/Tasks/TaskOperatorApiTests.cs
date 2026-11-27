using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Workers;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>El recorrido del operario (spec 2026-11-26 §6.7 y §7.9) contra el esquema real: pedir, confirmar al ledger, latir, soltar.
/// </summary>
public sealed class TaskOperatorApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>10 unidades en A-01-02, un movimiento de <paramref name="quantity"/> a B-01-01 creado por el supervisor y un operario.
    /// </summary>
    private sealed record Floor(LedgerSetup Setup, Guid Destination, TaskDto Task, Guid Worker, HttpClient Client);

    private static async Task<Floor> ArrangeFloorAsync(
        PostgreSqlContainer database, QepApiFactory factory, decimal quantity = 4)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, quantity)]));
        var (worker, client) = await SeedOperatorAsync(factory);
        return new Floor(s, destination, task, worker, client);
    }

    private static async Task<HttpResponseMessage> NextRawAsync(HttpClient client, Guid warehouseId, string device = "zebra-01") =>
        await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId, deviceId = device }, Ct);

    private static async Task<TaskDto> NextAsync(HttpClient client, Guid warehouseId)
    {
        var response = await NextRawAsync(client, warehouseId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<TaskDto>(response);
    }

    private static object ConfirmBody(
        TaskDto task,
        Guid token,
        decimal quantity,
        Guid? from = null,
        Guid? to = null,
        string? overrideReason = null,
        DateTimeOffset? occurredAt = null) =>
        new
        {
            leaseToken = token,
            fromLocationId = from ?? task.Lines[0].FromLocationId,
            toLocationId = to ?? task.Lines[0].ToLocationId,
            quantity,
            overrideReason,
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            deviceId = "zebra-01",
        };

    private static async Task<HttpResponseMessage> ConfirmRawAsync(
        HttpClient client, Guid taskId, Guid lineId, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TasksUrl()}/{taskId}/lines/{lineId}/confirm")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    private static Task<long> TaskMovementsAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Task'");

    // B3: la historia va en el orden del servidor y la confirmación tardía conserva su hora de escaneo.
    [Fact]
    public async Task TheHistoryFollowsTheServerOrderAndKeepsTheScanTime()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var scanned = DateTimeOffset.UtcNow;
        await ExecuteAsync(database, "UPDATE wms.tasks SET lease_expires_at = now() - interval '1 minute' WHERE id = @id", ("id", task.Id));
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, Ct);

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4, occurredAt: scanned));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = (await GetAsync(floor.Setup.Manager, task.Id)).Events;
        Assert.Equal(
            ["Created", "Published", "Claimed", "LeaseExpired", "LeaseResumed", "LineConfirmed", "Completed"],
            events.Select(value => value.Kind));
        Assert.True(events[4].OccurredAt < events[3].OccurredAt);
    }

    [Fact]
    public async Task NextReturnsTheTaskClaimedWithALeaseTokenAndRepeatingItIsTheSame()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var second = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        Assert.Equal((floor.Task.Id, "InProgress", floor.Worker), (first.Id, first.Status, first.ClaimedByMemberId));
        Assert.NotNull(first.LeaseToken);
        Assert.Equal((first.Id, first.LeaseToken, first.Version), (second.Id, second.LeaseToken, second.Version));
        // El supervisor ve quién la tiene, nunca su token.
        var seen = await GetAsync(floor.Setup.Manager, floor.Task.Id);
        Assert.Null(seen.LeaseToken);
        Assert.Equal(floor.Worker, seen.ClaimedByMemberId);
    }

    [Fact]
    public async Task NextWithoutEligibleTasksIs204()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await NextRawAsync(client, s.WarehouseId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task NextWithoutAnyTypePermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        using var reader = CreateClient(factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead);

        var response = await NextRawAsync(reader, floor.Setup.WarehouseId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmingMovesTheStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var done = await ReadAsync<TaskDto>(response);
        Assert.Equal("Done", done.Status);
        Assert.Null(done.LeaseToken);
        var variant = floor.Setup.VariantId;
        Assert.Equal(6m, await OnHandAsync(database, floor.Setup.BinA, variant, StockStatus.Available));
        Assert.Equal(4m, await OnHandAsync(database, floor.Destination, variant, StockStatus.Available));
        Assert.Equal(
            "InternalMove|zebra-01|" + task.Lines[0].Id,
            await ScalarAsync<string>(
                database,
                "SELECT reason || '|' || device_id || '|' || document_line_id::text FROM wms.stock_movements "
                    + "WHERE document_type = 'Task' AND document_id = @task",
                ("task", task.Id)));
    }

    [Fact]
    public async Task RepeatingAConfirmationReturnsTheSavedResponse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var body = ConfirmBody(task, task.LeaseToken!.Value, 2);
        var key = Guid.NewGuid().ToString();

        var first = await ConfirmRawAsync(floor.Client, task.Id, task.Lines[0].Id, body, key);
        var second = await ConfirmRawAsync(floor.Client, task.Id, task.Lines[0].Id, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync<TaskDto>(first)).Version, (await ReadAsync<TaskDto>(second)).Version);
        Assert.Equal(1L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task AConfirmationWithAStaleTokenIs409LeaseLost()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var (second, secondClient) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        using var _s = secondClient;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ActionAsync(
            floor.Setup.Manager, task.Id, "assign", new { memberId = second, version = task.Version, reason = "relevo" });

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        Assert.False(await ResumableOfAsync(response));
        Assert.Equal(0L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task TheSameKeyWithAnotherBodyOnConfirmIs409()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var key = Guid.NewGuid().ToString();
        var first = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 2), key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 3), key);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("wms.idempotency_key_reused", await CodeOfAsync(second));
        Assert.Equal(1L, await TaskMovementsAsync(database));
        Assert.Equal(2m, (await GetAsync(floor.Setup.Manager, task.Id)).Lines[0].QuantityConfirmed);
    }

    [Fact]
    public async Task AHeldAndRepublishedTaskRejectsALateConfirmation()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct);
        var held = await ActionAsync(
            floor.Setup.Manager, task.Id, "hold", new { version = (await GetAsync(floor.Setup.Manager, task.Id)).Version });
        await ActionAsync(floor.Setup.Manager, task.Id, "publish", new { version = held.Version });

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        Assert.False(await ResumableOfAsync(response));
        Assert.Equal(0L, await TaskMovementsAsync(database));
        var after = await GetAsync(floor.Setup.Manager, task.Id);
        Assert.Equal("Ready", after.Status);
        Assert.Null(after.ClaimedByMemberId);
    }

    private const string ExpireLeaseSql = "UPDATE wms.tasks SET lease_expires_at = now() - interval '1 minute' WHERE id = @id";

    [Fact]
    public async Task NextAfterTheLeaseExpiredWithoutASweepRenewsItWithTheSameToken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ExecuteAsync(database, ExpireLeaseSql, ("id", first.Id));

        var again = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        Assert.Equal((first.Id, first.LeaseToken, first.Version), (again.Id, again.LeaseToken, again.Version));
        Assert.True(again.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public async Task ClaimingMyOwnExpiredTaskWithoutASweepRenewsTheLease()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ExecuteAsync(database, ExpireLeaseSql, ("id", first.Id));

        var response = await ActionRawAsync(floor.Client, first.Id, "claim", new { deviceId = "zebra-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var again = await ReadAsync<TaskDto>(response);
        Assert.Equal((first.LeaseToken, first.Version), (again.LeaseToken, again.Version));
        Assert.True(again.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));
    }

    // Fija el comportamiento de hoy: CheckLease es Valid mientras la tarea esté InProgress, aunque el lease ya haya vencido.
    [Fact]
    public async Task AHeartbeatAfterTheLeaseExpiredWithoutASweepRenewsIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ExecuteAsync(database, ExpireLeaseSql, ("id", first.Id));

        var response = await ActionRawAsync(floor.Client, first.Id, "heartbeat", new { leaseToken = first.LeaseToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lease = await ReadAsync<TaskLeaseDto>(response);
        Assert.True(lease.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public async Task AHeartbeatAfterTheSweepIs409Resumable()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ExecuteAsync(database, ExpireLeaseSql, ("id", first.Id));
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, Ct);

        var response = await ActionRawAsync(floor.Client, first.Id, "heartbeat", new { leaseToken = first.LeaseToken });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        Assert.True(await ResumableOfAsync(response));
        var resumed = await ActionAsync(floor.Client, first.Id, "claim", new { deviceId = "zebra-01" });
        Assert.Equal(first.LeaseToken, resumed.LeaseToken);
    }

    // Soltar tras el barrido no se reanuda: sólo una confirmación trae un hecho físico. Responde 409 y la app puede retomar con claim.
    [Fact]
    public async Task AnUnclaimAfterTheSweepIs409Resumable()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var first = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        await ExecuteAsync(database, ExpireLeaseSql, ("id", first.Id));
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, Ct);

        var response = await ActionRawAsync(
            floor.Client, first.Id, "unclaim", new { leaseToken = first.LeaseToken, reason = "ShiftEnd" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.task.lease_lost", await CodeOfAsync(response));
        Assert.True(await ResumableOfAsync(response));
        Assert.Equal("Ready", (await GetAsync(floor.Setup.Manager, first.Id)).Status);
    }

    [Fact]
    public async Task ALateConfirmationResumesTheTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var scanned = DateTimeOffset.UtcNow;
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        Assert.Equal(1, await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct));

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 4, occurredAt: scanned));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Done", (await ReadAsync<TaskDto>(response)).Status);
        var history = (await GetAsync(floor.Setup.Manager, task.Id)).Events.Select(value => value.Kind).ToList();
        Assert.Contains("LeaseExpired", history);
        // Los tres de la confirmación van juntos y en orden causal.
        var resumed = history.IndexOf("LeaseResumed");
        Assert.Equal(["LeaseResumed", "LineConfirmed", "Completed"], history.Skip(resumed).Take(3));
    }

    // DA-32: contra el índice de «una tarea en curso por operario», que reanudar violaría.
    [Fact]
    public async Task ALateConfirmationWithAnotherTaskInProgressIsApplied()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var old = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        Assert.Equal(1, await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct));
        // M3: por id sólo se reclama lo que se le ofreció: la otra tarea nace asignada a este operario.
        var other = await CreateAsync(
            floor.Setup.Manager,
            Body(floor.Setup, [Line(floor.Setup.VariantId, floor.Setup.BinA, floor.Destination, 1)], assignTo: floor.Worker));
        await ActionAsync(floor.Client, other.Id, "claim", new { deviceId = "zebra-01" });

        var response = await ConfirmRawAsync(
            floor.Client, old.Id, old.Lines[0].Id, ConfirmBody(old, old.LeaseToken!.Value, 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await ReadAsync<TaskDto>(response);
        Assert.Equal(("Ready", 2m), (applied.Status, applied.Lines[0].QuantityConfirmed));
        Assert.Equal("InProgress", (await GetAsync(floor.Setup.Manager, other.Id)).Status);
        Assert.Equal(1L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task AWrongSourceIs422LocationMismatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ConfirmRawAsync(
            floor.Client,
            task.Id,
            task.Lines[0].Id,
            ConfirmBody(task, task.LeaseToken!.Value, 4, from: floor.Setup.Receiving));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.location_mismatch", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnotherDestinationWithReasonIsAccepted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var third = await CreateDestinationAsync(floor.Setup, "C-01-01");
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ConfirmRawAsync(
            floor.Client,
            task.Id,
            task.Lines[0].Id,
            ConfirmBody(task, task.LeaseToken!.Value, 4, to: third, overrideReason: "el destino estaba lleno"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4m, await OnHandAsync(database, third, floor.Setup.VariantId, StockStatus.Available));
        Assert.Equal(
            "el destino estaba lleno",
            await ScalarAsync<string>(
                database, "SELECT override_reason FROM wms.stock_movements WHERE document_type = 'Task'"));
    }

    [Fact]
    public async Task ConfirmingMoreThanTheStockIs422AndLeavesTheTaskAsItWas()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory, quantity: 12);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ConfirmRawAsync(
            floor.Client, task.Id, task.Lines[0].Id, ConfirmBody(task, task.LeaseToken!.Value, 12));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.stock.insufficient", await CodeOfAsync(response));
        var after = await GetAsync(floor.Setup.Manager, task.Id);
        Assert.Equal(("InProgress", task.Version), (after.Status, after.Version));
        Assert.Equal(0m, Assert.Single(after.Lines).QuantityConfirmed);
    }

    [Fact]
    public async Task UnclaimReturnsTheTaskToThePool()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var released = await ActionAsync(
            floor.Client, task.Id, "unclaim", new { leaseToken = task.LeaseToken, reason = "ShiftEnd" });

        Assert.Equal("Ready", released.Status);
        Assert.Null(released.ClaimedByMemberId);
    }

    [Fact]
    public async Task HeartbeatExtendsTheLeaseWithoutChangingTheVersion()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var task = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ActionRawAsync(floor.Client, task.Id, "heartbeat", new { leaseToken = task.LeaseToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lease = await ReadAsync<TaskLeaseDto>(response);
        Assert.True(lease.LeaseExpiresAt >= task.LeaseExpiresAt);
        Assert.Equal(task.Version, (await GetAsync(floor.Setup.Manager, task.Id)).Version);
    }

    [Fact]
    public async Task ClaimingATaskTakenBySomeoneElseIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var (_, secondClient) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        using var _s = secondClient;
        var taken = await NextAsync(floor.Client, floor.Setup.WarehouseId);

        var response = await ActionRawAsync(secondClient, taken.Id, "claim", new { deviceId = "zebra-02" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.invalid_transition", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ClaimingASecondTaskIs409MemberBusyWithTheCurrentOne()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var other = await CreateAsync(
            floor.Setup.Manager,
            Body(floor.Setup, [Line(floor.Setup.VariantId, floor.Setup.BinA, floor.Destination, 1)]));
        var current = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var second = current.Id == floor.Task.Id ? other.Id : floor.Task.Id;

        var response = await ActionRawAsync(floor.Client, second, "claim", new { deviceId = "zebra-01" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = System.Text.Json.JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("wms.task.member_busy", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(current.Id, problem.RootElement.GetProperty("taskId").GetGuid());
    }

    // M3: el reclamo por id de una tarea del pool no se ofrece; el trabajo nuevo llega por «siguiente».
    [Fact]
    public async Task ClaimingAPoolTaskByIdIs422NotOffered()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ActionRawAsync(floor.Client, floor.Task.Id, "claim", new { deviceId = "zebra-01" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.not_offered", await CodeOfAsync(response));
        Assert.Equal("Ready", (await GetAsync(floor.Setup.Manager, floor.Task.Id)).Status);
    }

    [Fact]
    public async Task MineShowsTheCurrentAndTheAssigned()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var current = await NextAsync(floor.Client, floor.Setup.WarehouseId);
        var assigned = await CreateAsync(
            floor.Setup.Manager,
            Body(
                floor.Setup,
                [Line(floor.Setup.VariantId, floor.Setup.BinA, floor.Destination, 1)],
                assignTo: floor.Worker));

        var mine = await floor.Client.GetFromJsonAsync<MyTasksDto>(
            $"{TasksUrl()}/mine?warehouseId={floor.Setup.WarehouseId}", Ct);

        Assert.NotNull(mine);
        Assert.Equal((current.Id, current.LeaseToken), (mine.Current!.Id, mine.Current.LeaseToken));
        Assert.Equal(assigned.Id, Assert.Single(mine.Assigned).Id);
    }
}
