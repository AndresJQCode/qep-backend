using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskCountApiTests;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>La acción <c>count</c> y el gancho de cierre del <c>Count</c> (spec 2026-11-26 §8.4, DA-40 a DA-47): contar lo que el
/// sistema tiene resuelve las novedades; contar otra cosa propone un ajuste <c>CountVariance</c> y nunca toca el ledger. Parte de
/// <see cref="TaskCountApiTests.ArrangeShortageAsync"/>: el sistema tiene 8 en A-01-02.</summary>
public sealed class TaskCountRecordingApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Counting(Shortage Shortage, Guid Counter, HttpClient Client, TaskDto Count);

    private static async Task<Counting> ArrangeCountingAsync(PostgreSqlContainer database, QepApiFactory factory, decimal found = 2)
    {
        var shortage = await ArrangeShortageAsync(database, factory, found);
        var (counter, client) = await SeedCounterAsync(factory);
        var count = await ReadAsync<TaskDto>(await NextRawAsync(client, shortage.Setup.WarehouseId));
        Assert.Equal(shortage.CountId, count.Id);
        return new Counting(shortage, counter, client, count);
    }

    private static Task<HttpResponseMessage> CountAsync(
        Counting c, decimal counted, Guid? lot = null, Guid? location = null, string? key = null, DateTimeOffset? occurredAt = null) =>
        CountRawAsync(
            c.Client,
            c.Count.Id,
            c.Count.Lines[0].Id,
            CountBody(c.Count.LeaseToken!.Value, location ?? c.Shortage.Setup.BinA, counted, lot, occurredAt),
            key ?? NewKey());

    private static Task<long> LongAsync(PostgreSqlContainer database, string sql, params (string, object?)[] parameters) =>
        ScalarAsync<long>(database, sql, parameters);

    private static Task<long> MovementsAsync(PostgreSqlContainer database) =>
        LongAsync(database, "SELECT count(*) FROM wms.stock_movements");

    private static Task<string> TaskStatusAsync(PostgreSqlContainer database, Guid id) =>
        ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", id));

    [Fact]
    public async Task CountingWhatTheSystemHasClosesTheIncidentsWithoutAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var movements = await MovementsAsync(database);

        var response = await CountAsync(c, 8);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadAsync<TaskDto>(response);
        Assert.Equal(("Done", "Done", 8m), (dto.Status, dto.Lines[0].LineStatus, dto.Lines[0].QuantityCounted));
        Assert.Equal(
            ("Resolved", "Accept", Guid.Empty),
            (await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", c.Shortage.IncidentId)),
             await ScalarAsync<string>(database, "SELECT resolution FROM wms.incidents WHERE id = @id", ("id", c.Shortage.IncidentId)),
             await ScalarAsync<Guid>(database, "SELECT resolved_by FROM wms.incidents WHERE id = @id", ("id", c.Shortage.IncidentId))));
        Assert.Equal(0L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(0L, await LongAsync(database, "SELECT count(*) FROM wms.incidents WHERE type = 'CountVariance'"));
        Assert.Equal(movements, await MovementsAsync(database));
        Assert.Equal(8m, await ScalarAsync<decimal>(database, "SELECT quantity_counted FROM wms.task_lines WHERE task_id = @id", ("id", c.Count.Id)));
    }

    private static async Task<(Guid AdjustmentId, Guid VarianceId)> AssertVarianceAsync(
        PostgreSqlContainer database, QepApiFactory factory, Counting c, decimal counted, decimal delta, string key)
    {
        var adjustment = await ScalarAsync<Guid>(database, "SELECT id FROM wms.adjustments");
        Assert.Equal(1L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(
            ("CountVariance", "Proposed"),
            (await ScalarAsync<string>(database, "SELECT reason FROM wms.adjustments"),
             await ScalarAsync<string>(database, "SELECT status FROM wms.adjustments")));
        Assert.Equal(delta, await ScalarAsync<decimal>(database, "SELECT quantity_delta FROM wms.adjustment_lines"));
        Assert.Equal(c.Shortage.Setup.BinA, await ScalarAsync<Guid>(database, "SELECT location_id FROM wms.adjustment_lines"));
        var variance = await ScalarAsync<Guid>(database, "SELECT id FROM wms.incidents WHERE type = 'CountVariance'");
        Assert.Equal(
            ("Open", adjustment, 8m, counted, c.Counter, key),
            (await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", variance)),
             await ScalarAsync<Guid>(database, "SELECT adjustment_id FROM wms.incidents WHERE id = @id", ("id", variance)),
             await ScalarAsync<decimal>(database, "SELECT expected_qty FROM wms.incidents WHERE id = @id", ("id", variance)),
             await ScalarAsync<decimal>(database, "SELECT actual_qty FROM wms.incidents WHERE id = @id", ("id", variance)),
             await ScalarAsync<Guid>(database, "SELECT reported_by FROM wms.incidents WHERE id = @id", ("id", variance)),
             await ScalarAsync<string>(database, "SELECT operation_key FROM wms.incidents WHERE id = @id", ("id", variance))));
        Assert.Equal(
            ("Resolved", variance),
            (await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", c.Shortage.IncidentId)),
             await ScalarAsync<Guid>(database, "SELECT parent_incident_id FROM wms.incidents WHERE id = @id", ("id", c.Shortage.IncidentId))));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.proposed", adjustment.ToString()));
        return (adjustment, variance);
    }

    [Fact]
    public async Task CountingLessProposesACountVarianceAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var movements = await MovementsAsync(database);
        var key = NewKey();

        var response = await CountAsync(c, 5, key: key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertVarianceAsync(database, factory, c, 5, -3, key);
        // El conteo nunca escribe al ledger: el movimiento lo hará el ajuste, ya aprobado.
        Assert.Equal(movements, await MovementsAsync(database));
        Assert.Equal(8m, await OnHandAsync(database, c.Shortage.Setup.BinA, c.Shortage.Setup.VariantId, Modules.Wms.Domain.Inventory.StockStatus.Available));
    }

    [Fact]
    public async Task CountingMoreProposesAPositiveDelta()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var key = NewKey();

        Assert.Equal(HttpStatusCode.OK, (await CountAsync(c, 9, key: key)).StatusCode);

        await AssertVarianceAsync(database, factory, c, 9, 1, key);
    }

    [Fact]
    public async Task CountingZeroIsValid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var key = NewKey();

        Assert.Equal(HttpStatusCode.OK, (await CountAsync(c, 0, key: key)).StatusCode);

        await AssertVarianceAsync(database, factory, c, 0, -8, key);
        Assert.Equal(0m, await ScalarAsync<decimal>(database, "SELECT quantity_counted FROM wms.task_lines WHERE task_id = @id", ("id", c.Count.Id)));
    }

    // Una variante por lote, un faltante con cero encontrado (el Count nace sin lote) y 10 del lote L1 en la ubicación.
    private static async Task<(Counting Counting, Guid Lot)> ArrangeLotCountingAsync(PostgreSqlContainer database, QepApiFactory factory)
    {
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var lot = await CreateLotAsync(factory, s, s.VariantId, "L1");
        await StockAsync(factory, s, s.BinA, 10, lot);
        var destination = await CreateDestinationAsync(s);
        var move = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (reporter, reporterClient) = await SeedOperatorAsync(factory);
        var next = await ReadAsync<TaskDto>(await NextRawAsync(reporterClient, s.WarehouseId));
        Assert.Equal(
            HttpStatusCode.OK,
            (await ShortRawAsync(reporterClient, next.Id, next.Lines[0].Id, ShortBody(next.LeaseToken!.Value, 0), NewKey())).StatusCode);
        var countId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count'");
        var incidentId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.incidents WHERE source_task_id = @id", ("id", move.Id));
        var (counter, client) = await SeedCounterAsync(factory);
        var count = await ReadAsync<TaskDto>(await NextRawAsync(client, s.WarehouseId));
        var shortage = new Shortage(s, destination, move, reporter, reporterClient, countId, incidentId);
        return (new Counting(shortage, counter, client, count), lot);
    }

    [Fact]
    public async Task CountingALotLineUsesTheScannedLot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (c, lot) = await ArrangeLotCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        Assert.Null(c.Count.Lines[0].LotId);

        var response = await CountAsync(c, 7, lot);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(lot, (await ReadAsync<TaskDto>(response)).Lines[0].LotId);
        // Lo esperado se leyó para L1 (10), no para «sin lote» (0).
        Assert.Equal(-3m, await ScalarAsync<decimal>(database, "SELECT quantity_delta FROM wms.adjustment_lines"));
        Assert.Equal(lot, await ScalarAsync<Guid>(database, "SELECT lot_id FROM wms.adjustment_lines"));
    }

    [Fact]
    public async Task ALotTrackedVariantWithoutLotIs422LotRequired()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (c, _lot) = await ArrangeLotCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;

        var response = await CountAsync(c, 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.lot.required", await CodeOfAsync(response));
        Assert.Equal("InProgress", await TaskStatusAsync(database, c.Count.Id));
        Assert.Equal(0L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task CountingASerialVariantIs422SerialRequired()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Serial");
        var seeded = NewCount(s, s.BinA);
        await SaveAsync(factory, seeded);
        var (_, client) = await SeedCounterAsync(factory);
        using var _c = client;
        var count = await ReadAsync<TaskDto>(await NextRawAsync(client, s.WarehouseId));
        Assert.Equal(seeded.Id, count.Id);

        var response = await CountRawAsync(
            client, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, s.BinA, 3), NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.serial.required", await CodeOfAsync(response));
        Assert.Equal("InProgress", await TaskStatusAsync(database, count.Id));
        Assert.Equal(0L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task RepeatingTheCountReturnsTheSavedResponse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var key = NewKey();
        var body = CountBody(c.Count.LeaseToken!.Value, c.Shortage.Setup.BinA, 5);

        var first = await CountRawAsync(c.Client, c.Count.Id, c.Count.Lines[0].Id, body, key);
        var second = await CountRawAsync(c.Client, c.Count.Id, c.Count.Lines[0].Id, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await BodyAsync(first)), JsonNode.Parse(await BodyAsync(second))));
        Assert.Equal(1L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(1L, await LongAsync(database, "SELECT count(*) FROM wms.incidents WHERE type = 'CountVariance'"));
    }

    [Fact]
    public async Task AWrongLocationIs422LocationMismatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;

        var response = await CountAsync(c, 5, location: c.Shortage.Destination);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.location_mismatch", await CodeOfAsync(response));
        Assert.Equal("InProgress", await TaskStatusAsync(database, c.Count.Id));
    }

    [Fact]
    public async Task ConfirmingOrShortingACountIs422InvalidTransition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;
        var token = c.Count.LeaseToken!.Value;
        using var confirm = new HttpRequestMessage(
            HttpMethod.Post, $"{TasksUrl()}/{c.Count.Id}/lines/{c.Count.Lines[0].Id}/confirm")
        {
            Content = JsonContent.Create(new
            {
                leaseToken = token,
                fromLocationId = c.Shortage.Setup.BinA,
                toLocationId = c.Shortage.Destination,
                quantity = 1,
                occurredAt = DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
        };
        confirm.Headers.Add("Idempotency-Key", NewKey());

        var confirmed = await c.Client.SendAsync(confirm, Ct);
        var shorted = await ShortRawAsync(c.Client, c.Count.Id, c.Count.Lines[0].Id, ShortBody(token, 0), NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirmed.StatusCode);
        Assert.Equal("wms.task.invalid_transition", await CodeOfAsync(confirmed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shorted.StatusCode);
        Assert.Equal("wms.task.invalid_transition", await CodeOfAsync(shorted));
        Assert.Equal("InProgress", await TaskStatusAsync(database, c.Count.Id));
    }

    [Fact]
    public async Task CountingAMoveIs422InvalidTransition()
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
        var move = await ReadAsync<TaskDto>(await NextRawAsync(client, s.WarehouseId));

        var response = await CountRawAsync(
            client, move.Id, move.Lines[0].Id, CountBody(move.LeaseToken!.Value, s.BinA, 5), NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.task.invalid_transition", await CodeOfAsync(response));
        Assert.Equal("InProgress", await TaskStatusAsync(database, move.Id));
    }

    [Fact]
    public async Task ACountScanBeforeTheReleaseFloorIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;

        var response = await CountAsync(c, 5, occurredAt: c.Count.ReleasedAt!.Value.AddMinutes(-6));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.movement.occurred_at_out_of_range", await CodeOfAsync(response));
        Assert.Equal(0L, await LongAsync(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task CountingWithoutCountPerformIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var c = await ArrangeCountingAsync(database, factory);
        using var _r = c.Shortage.ReporterClient;
        using var _c = c.Client;

        // El reportante sólo tiene PutawayPerform: la ruta lo deja pasar y el handler le pide el permiso del tipo de la tarea.
        var response = await CountRawAsync(
            c.Shortage.ReporterClient, c.Count.Id, c.Count.Lines[0].Id, CountBody(c.Count.LeaseToken!.Value, c.Shortage.Setup.BinA, 5), NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("InProgress", await TaskStatusAsync(database, c.Count.Id));
    }

    private static async Task<string> UnlinkedIncidentAsync(PostgreSqlContainer database, Guid incidentId)
    {
        Assert.Equal(
            "Open", await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", incidentId)));
        Assert.Equal(
            0L,
            await LongAsync(database, "SELECT count(*) FROM wms.incidents WHERE id = @id AND verification_task_id IS NOT NULL", ("id", incidentId)));
        return await ScalarAsync<string>(
            database,
            "SELECT kind FROM wms.incident_events WHERE incident_id = @id AND kind = 'VerificationCancelled'",
            ("id", incidentId));
    }

    [Fact]
    public async Task CancellingTheCountUnlinksItsIncidents()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var count = await GetAsync(shortage.Setup.Manager, shortage.CountId);

        var cancelled = await ActionRawAsync(
            shortage.Setup.Manager, shortage.CountId, "cancel", new { reason = "ya no hace falta", version = count.Version });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("VerificationCancelled", await UnlinkedIncidentAsync(database, shortage.IncidentId));
    }

    [Fact]
    public async Task CancellingTheOnlyLineOfTheCountDoesTheSame()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var count = await GetAsync(shortage.Setup.Manager, shortage.CountId);

        var cancelled = await ActionRawAsync(
            shortage.Setup.Manager,
            shortage.CountId,
            $"lines/{count.Lines[0].Id}/cancel",
            new { reason = "ya no hace falta", version = count.Version });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", await TaskStatusAsync(database, shortage.CountId));
        Assert.Equal("VerificationCancelled", await UnlinkedIncidentAsync(database, shortage.IncidentId));
    }

    [Fact]
    public async Task AnotherShortAfterTheCountIsCancelledCreatesANewCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var count = await GetAsync(shortage.Setup.Manager, shortage.CountId);
        await ActionAsync(shortage.Setup.Manager, shortage.CountId, "cancel", new { reason = "ya no hace falta", version = count.Version });
        await CreateAsync(shortage.Setup.Manager, Body(shortage.Setup, [Line(shortage.Setup.VariantId, shortage.Setup.BinA, shortage.Destination, 3)]));
        var next = await ReadAsync<TaskDto>(await NextRawAsync(shortage.ReporterClient, shortage.Setup.WarehouseId));

        var response = await ShortRawAsync(
            shortage.ReporterClient, next.Id, next.Lines[0].Id, ShortBody(next.LeaseToken!.Value, 1), NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2L, await LongAsync(database, "SELECT count(*) FROM wms.tasks WHERE type = 'Count'"));
        Assert.Equal(1L, await LongAsync(database, "SELECT count(*) FROM wms.tasks WHERE type = 'Count' AND status = 'Ready'"));
        Assert.Equal(
            1L,
            await LongAsync(database, "SELECT count(*) FROM wms.incidents WHERE verification_task_id IS NOT NULL"));
    }
}
