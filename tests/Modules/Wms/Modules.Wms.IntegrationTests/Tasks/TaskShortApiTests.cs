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

/// <summary>El faltante en una línea (spec 2026-11-26 §6.8 y §8.4): <c>short</c> confirma lo encontrado, cierra la línea como
/// <c>Short</c> y deja la novedad en la misma transacción, con la misma <c>Idempotency-Key</c>.</summary>
public sealed class TaskShortApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Floor(LedgerSetup Setup, Guid Destination, TaskDto Task, Guid Worker, HttpClient Client);

    /// <summary>10 unidades en A-01-02, un movimiento de <paramref name="quantity"/> a B-01-01 y un operario que ya lo reclamó.</summary>
    private static async Task<Floor> ArrangeFloorAsync(
        PostgreSqlContainer database, QepApiFactory factory, decimal quantity = 5, Guid? lot = null)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10, lot);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, quantity, lot)]));
        var (worker, client) = await SeedOperatorAsync(factory);
        var response = await client.PostAsJsonAsync(
            $"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Floor(s, destination, await ReadAsync<TaskDto>(response), worker, client);
    }

    private static string NewKey() => Guid.NewGuid().ToString();

    private static Task<long> TaskMovementsAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Task'");

    private static Task<long> IncidentCountAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.incidents");

    private static async Task<HttpResponseMessage> ShortAsync(
        Floor floor, decimal found, string type = "ShortMove", Guid? lotId = null, DateTimeOffset? occurredAt = null, string? key = null) =>
        await ShortRawAsync(
            floor.Client,
            floor.Task.Id,
            floor.Task.Lines[0].Id,
            ShortBody(floor.Task.LeaseToken!.Value, found, type, lotId, occurredAt),
            key ?? NewKey());

    [Fact]
    public async Task ShortWithTwoFoundMovesTwoAndClosesTheTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var scanned = DateTimeOffset.UtcNow.AddSeconds(-30);
        var key = NewKey();

        var response = await ShortAsync(floor, 2, occurredAt: scanned, key: key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadAsync<TaskDto>(response);
        Assert.Equal("Done", dto.Status);
        Assert.Equal(("Short", 2m), (dto.Lines[0].LineStatus, dto.Lines[0].QuantityConfirmed));
        Assert.Equal(2m, await OnHandAsync(database, floor.Destination, floor.Setup.VariantId, StockStatus.Available));
        Assert.Equal(8m, await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available));
        var incident = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        Assert.Equal(("ShortMove", "Open", 5m, 2m), (incident.Type, incident.Status, incident.ExpectedQty, incident.ActualQty));
        Assert.Equal((floor.Worker, key), (incident.ReportedBy, incident.OperationKey));
        Assert.Equal(floor.Task.Lines[0].Id, incident.SourceTaskLineId);
        Assert.Equal(scanned, incident.ReportedAt, TimeSpan.FromMilliseconds(1));
        Assert.Null(incident.VerificationTaskId);
    }

    [Fact]
    public async Task ShortWithNothingFoundMovesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Done", (await ReadAsync<TaskDto>(response)).Status);
        Assert.Equal(0L, await TaskMovementsAsync(database));
        Assert.Equal(10m, await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available));
        Assert.Equal(0m, Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id)).ActualQty);
    }

    [Fact]
    public async Task ShortWithALotMovesThatLot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var lot = await CreateLotAsync(factory, s, s.VariantId, "L1");
        await StockAsync(factory, s, s.BinA, 10, lot);
        var destination = await CreateDestinationAsync(s);
        var created = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var next = await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct);
        var task = await ReadAsync<TaskDto>(next);
        Assert.Equal(created.Id, task.Id);

        var response = await ShortRawAsync(
            client, task.Id, task.Lines[0].Id, ShortBody(task.LeaseToken!.Value, 2, lotId: lot), NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2m, await OnHandAsync(database, destination, s.VariantId, StockStatus.Available, lot));
        Assert.Equal(lot, Assert.Single(await IncidentsOfTaskAsync(database, task.Id)).LotId);
    }

    [Fact]
    public async Task AShortWithAnotherLotThanTheLineIs422LotMismatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var planned = await CreateLotAsync(factory, s, s.VariantId, "L1");
        var other = await CreateLotAsync(factory, s, s.VariantId, "L2");
        await StockAsync(factory, s, s.BinA, 10, planned);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5, planned)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));

        var response = await ShortRawAsync(
            client, task.Id, task.Lines[0].Id, ShortBody(task.LeaseToken!.Value, 0, lotId: other), NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.lot_mismatch", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task AShortWithNothingFoundOnALotVariantNeedsNoLot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var lot = await CreateLotAsync(factory, s, s.VariantId, "L1");
        await StockAsync(factory, s, s.BinA, 10, lot);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));

        var response = await ShortRawAsync(
            client, task.Id, task.Lines[0].Id, ShortBody(task.LeaseToken!.Value, 0), NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(await IncidentsOfTaskAsync(database, task.Id)).LotId);
    }

    [Fact]
    public async Task AForeignLotIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var otherVariant = await CreateVariantAsync(factory, "V2", null, [12]);
        var foreign = await CreateLotAsync(factory, floor.Setup, otherVariant, "LX");

        var response = await ShortAsync(floor, 0, lotId: foreign);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.lot.not_found", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task RepeatingTheShortReturnsTheSavedResponse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var key = NewKey();
        var body = ShortBody(floor.Task.LeaseToken!.Value, 2);

        var first = await ShortRawAsync(floor.Client, floor.Task.Id, floor.Task.Lines[0].Id, body, key);
        var second = await ShortRawAsync(floor.Client, floor.Task.Id, floor.Task.Lines[0].Id, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // El recibo se guarda como jsonb: el texto vuelve con otros espacios, el contenido es el mismo.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await BodyAsync(first)), JsonNode.Parse(await BodyAsync(second))));
        Assert.Equal(1L, await IncidentCountAsync(database));
        Assert.Equal(1L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task ATypeNotDeclarableOnAMoveIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 2, "UnreadableLabel");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.type_not_allowed", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
        Assert.Equal(0L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task AnUnknownTypeIs422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 2, "Nope");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task FindingEverythingIs422LineInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 5);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.line_invalid", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task AScanMoreThanFiveMinutesBeforeTheReleaseIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 2, occurredAt: floor.Task.ReleasedAt!.Value.AddMinutes(-6));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.movement.occurred_at_out_of_range", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
        Assert.Equal(0L, await TaskMovementsAsync(database));
    }

    [Fact]
    public async Task ShortMoreThanTheStockRollsBackEverything()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        await ExecuteAsync(
            database,
            "UPDATE wms.stock_balances SET on_hand = 1 WHERE location_id = @location AND variant_id = @variant",
            ("location", floor.Setup.BinA),
            ("variant", floor.Setup.VariantId));

        var response = await ShortAsync(floor, 2);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.stock.insufficient", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
        Assert.Equal("Open", (await GetAsync(floor.Setup.Manager, floor.Task.Id)).Lines[0].LineStatus);
    }

    [Fact]
    public async Task ShortWithoutThePutawayPermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        using var reader = CreateClient(factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead);

        var response = await ShortRawAsync(
            reader, floor.Task.Id, floor.Task.Lines[0].Id, ShortBody(floor.Task.LeaseToken!.Value, 2), NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task ShortWithoutTheIdempotencyKeyIs428()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortRawAsync(
            floor.Client, floor.Task.Id, floor.Task.Lines[0].Id, ShortBody(floor.Task.LeaseToken!.Value, 2));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task ALateShortOfTheSameOperatorResumesTheTask()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<TaskLeaseSweeper>().Single();
        Assert.Equal(1, await sweeper.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(16), Ct));

        var response = await ShortAsync(floor, 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = (await GetAsync(floor.Setup.Manager, floor.Task.Id)).Events.Select(value => value.Kind).ToList();
        var resumed = history.IndexOf("LeaseResumed");
        Assert.Equal(["LeaseResumed", "LineConfirmed", "LineShort", "Completed"], history.Skip(resumed).Take(4));
    }
}
