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
        Floor floor,
        decimal found,
        string type = "ShortMove",
        Guid? lotId = null,
        DateTimeOffset? occurredAt = null,
        string? key = null,
        IReadOnlyList<Guid>? photos = null) =>
        await ShortRawAsync(
            floor.Client,
            floor.Task.Id,
            floor.Task.Lines[0].Id,
            ShortBody(floor.Task.LeaseToken!.Value, found, type, lotId, occurredAt, photos: photos),
            key ?? NewKey());

    private static Task<long> EvidenceCountAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.incident_evidence");

    private static async Task AssertEvidenceInvalidAsync(HttpResponseMessage response, PostgreSqlContainer database)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.evidence_invalid", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
        Assert.Equal(0L, await EvidenceCountAsync(database));
        Assert.Equal(0L, await TaskMovementsAsync(database));
    }

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
        // Un faltante pide un conteo de verificación (C3): la novedad ya lo tiene enlazado.
        Assert.NotNull(incident.VerificationTaskId);
    }

    private static Task<long> CountTasksAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks WHERE type = 'Count'");

    [Fact]
    public async Task AShortCreatesAReadyCountOfTheSourceKey()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var incident = Assert.Single(await IncidentsOfTaskAsync(database, floor.Task.Id));
        var countId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count'");
        Assert.Equal(countId, incident.VerificationTaskId);
        var count = await GetAsync(floor.Setup.Manager, countId);
        Assert.Equal(("Count", "Ready", "Incident", incident.Id), (count.Type, count.Status, count.SourceType, count.SourceId));
        var line = Assert.Single(count.Lines);
        Assert.Equal(("A-01-02", "V1", "Available", 0m, 0m), (line.FromLocationCode, line.VariantCode, line.StockStatus, line.QuantityPlanned, line.QuantityRemaining));
        Assert.Null(line.ToLocationId);
        Assert.Null(line.LotId);
        Assert.Null(line.QuantityCounted);
        Assert.Equal(1L, await CountTasksAsync(database));
        Assert.Equal(
            "VerificationLinked",
            await ScalarAsync<string>(database, "SELECT kind FROM wms.incident_events WHERE kind = 'VerificationLinked'"));
    }

    // Dos operarios, dos movimientos que salen de la misma ubicación, los dos con su tarea reclamada.
    private sealed record TwoShorts(LedgerSetup Setup, TaskDto First, HttpClient FirstClient, TaskDto Second, HttpClient SecondClient);

    private static async Task<TwoShorts> ArrangeTwoShortsAsync(QepApiFactory factory, PostgreSqlContainer database)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)], priority: 5));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 4)], priority: 4));
        return await ClaimTwoAsync(factory, s);
    }

    private static async Task<TwoShorts> ClaimTwoAsync(QepApiFactory factory, LedgerSetup s)
    {
        var (_, first) = await SeedOperatorAsync(factory);
        var (_, second) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        var firstTask = await ReadAsync<TaskDto>(await NextRawAsync(first, s.WarehouseId));
        var secondTask = await ReadAsync<TaskDto>(await NextRawAsync(second, s.WarehouseId));
        return new TwoShorts(s, firstTask, first, secondTask, second);
    }

    // Con un lote planeado, el escaneo tiene que traerlo: el de la línea.
    private static Task<HttpResponseMessage> ShortOfAsync(HttpClient client, TaskDto task, decimal found, string key) =>
        ShortRawAsync(
            client, task.Id, task.Lines[0].Id, ShortBody(task.LeaseToken!.Value, found, lotId: task.Lines[0].LotId), key);

    [Fact]
    public async Task ASecondShortOnTheSameKeyLinksTheOpenCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var two = await ArrangeTwoShortsAsync(factory, database);
        using var _a = two.FirstClient;
        using var _b = two.SecondClient;

        Assert.Equal(HttpStatusCode.OK, (await ShortOfAsync(two.FirstClient, two.First, 2, NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ShortOfAsync(two.SecondClient, two.Second, 1, NewKey())).StatusCode);

        Assert.Equal(1L, await CountTasksAsync(database));
        var countId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count'");
        Assert.Equal(2L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.incidents WHERE verification_task_id = @count", ("count", countId)));
    }

    [Fact]
    public async Task TwoConcurrentShortsOnTheSameKeyShareOneCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var two = await ArrangeTwoShortsAsync(factory, database);
        using var _a = two.FirstClient;
        using var _b = two.SecondClient;

        var responses = await Task.WhenAll(
            ShortOfAsync(two.FirstClient, two.First, 2, NewKey()),
            ShortOfAsync(two.SecondClient, two.Second, 1, NewKey()));

        // El count-key serializa a los dos: el segundo encuentra el Count del primero y no crea otro.
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1L, await CountTasksAsync(database));
        Assert.Equal(2L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.incidents WHERE verification_task_id IS NOT NULL"));
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(DISTINCT verification_task_id) FROM wms.incidents"));
    }

    [Fact]
    public async Task AShortOnAnotherLotCreatesAnotherCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var lotOne = await CreateLotAsync(factory, s, s.VariantId, "L1");
        var lotTwo = await CreateLotAsync(factory, s, s.VariantId, "L2");
        var two = await ArrangeTwoShortsOnLotsAsync(factory, s, lotOne, lotTwo);
        using var _a = two.FirstClient;
        using var _b = two.SecondClient;

        Assert.Equal(HttpStatusCode.OK, (await ShortOfAsync(two.FirstClient, two.First, 2, NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ShortOfAsync(two.SecondClient, two.Second, 1, NewKey())).StatusCode);

        Assert.Equal(2L, await CountTasksAsync(database));
        foreach (var lot in new[] { lotOne, lotTwo })
        {
            Assert.Equal(1L, await ScalarAsync<long>(
                database, "SELECT count(*) FROM wms.task_lines WHERE quantity_planned = 0 AND lot_id = @lot", ("lot", lot)));
        }
    }

    private static async Task<TwoShorts> ArrangeTwoShortsOnLotsAsync(QepApiFactory factory, LedgerSetup s, Guid lotOne, Guid lotTwo)
    {
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10, lotOne);
        await StockAsync(factory, s, s.BinA, 10, lotTwo);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5, lotOne)], priority: 5));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 4, lotTwo)], priority: 4));
        return await ClaimTwoAsync(factory, s);
    }

    [Fact]
    public async Task AShortOnASerialVariantCreatesNoCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Serial");
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(await NextRawAsync(client, s.WarehouseId));

        // Con cero encontrado no hay llamada al ledger (que pediría los seriales): DA-44, a una variante de series no se le genera Count.
        var response = await ShortOfAsync(client, task, 0, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, await CountTasksAsync(database));
        Assert.Null(Assert.Single(await IncidentsOfTaskAsync(database, task.Id)).VerificationTaskId);
    }

    [Fact]
    public async Task TheCountAndItsIncidentRollBackWithTheShort()
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
        Assert.Equal(0L, await CountTasksAsync(database));
        Assert.Equal(0L, await IncidentCountAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.incident_events"));
    }

    [Fact]
    public async Task RepeatingTheShortCreatesNoSecondCount()
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
        Assert.Equal(1L, await CountTasksAsync(database));
        Assert.Equal(1L, await IncidentCountAsync(database));
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
    public async Task AShortWithNothingFoundOnAFixedLotLineNeedsNoScannedLot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var planned = await CreateLotAsync(factory, s, s.VariantId, "L1");
        await StockAsync(factory, s, s.BinA, 10, planned);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5, planned)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var task = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));

        // No encontró nada, así que no escaneó ningún lote: no hay con qué comparar el de la línea.
        var response = await ShortRawAsync(
            client, task.Id, task.Lines[0].Id, ShortBody(task.LeaseToken!.Value, 0), NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(planned, Assert.Single(await IncidentsOfTaskAsync(database, task.Id)).LotId);
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

    [Fact]
    public async Task AShortKeepsItsPhotosEvenWhileUploading()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var tenant = Guid.Parse(TenantId);
        Guid[] photos = [await SeedEvidenceFileAsync(factory, tenant), await SeedEvidenceFileAsync(factory, tenant)];

        var response = await ShortAsync(floor, 2, photos: photos);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2L, await EvidenceCountAsync(database));
        foreach (var photo in photos)
        {
            Assert.Equal(1L, await ScalarAsync<long>(
                database, "SELECT count(*) FROM wms.incident_evidence WHERE file_id = @id AND kind = 'Photo'", ("id", photo)));
        }
    }

    [Fact]
    public async Task APhotoOfAnotherTenantIs422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        await SeedTenantAsync(factory, OtherTenantId);
        var foreign = await SeedEvidenceFileAsync(factory, Guid.Parse(OtherTenantId));
        var own = await SeedEvidenceFileAsync(factory, Guid.Parse(TenantId));

        var response = await ShortAsync(floor, 2, photos: [own, foreign]);

        await AssertEvidenceInvalidAsync(response, database);
    }

    [Fact]
    public async Task AFileThatIsNotEvidenceIs422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var export = await SeedEvidenceFileAsync(factory, Guid.Parse(TenantId), Modules.Storage.Domain.FileOwnerType.WmsExport);

        var response = await ShortAsync(floor, 2, photos: [export]);

        await AssertEvidenceInvalidAsync(response, database);
    }

    [Fact]
    public async Task AnUnknownPhotoIs422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;

        var response = await ShortAsync(floor, 2, photos: [Guid.NewGuid()]);

        await AssertEvidenceInvalidAsync(response, database);
    }

    [Fact]
    public async Task SixPhotosAre422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var photos = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            photos.Add(await SeedEvidenceFileAsync(factory, Guid.Parse(TenantId)));
        }

        var response = await ShortAsync(floor, 2, photos: photos);

        await AssertEvidenceInvalidAsync(response, database);
    }

    [Fact]
    public async Task TheSamePhotoTwiceCountsOnce()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeFloorAsync(database, factory);
        using var _c = floor.Client;
        var photo = await SeedEvidenceFileAsync(factory, Guid.Parse(TenantId));

        var response = await ShortAsync(floor, 2, photos: [photo, photo, photo, photo, photo, photo]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await EvidenceCountAsync(database));
    }
}
