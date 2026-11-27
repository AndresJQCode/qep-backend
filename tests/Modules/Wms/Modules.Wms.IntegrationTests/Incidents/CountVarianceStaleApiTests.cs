using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskCountApiTests;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>El ajuste <c>CountVariance</c> sólo vale mientras el saldo siga siendo el que el conteo vio (decisión del owner,
/// 2026-11-27, H1): si cambió, al aprobar o contabilizar el sistema rechaza el ajuste, pide un conteo nuevo de la misma clave, enlaza
/// a él las novedades y responde 422 <c>wms.adjustment.count_stale</c>. Parten del conteo de 5 contra 8: el ajuste es −3 sobre A-01-02.</summary>
public sealed class CountVarianceStaleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ApproverSubject = "01900000-0000-7000-8000-000000000006";

    private static Task<HttpClient> ApproverAsync(QepApiFactory factory) =>
        SeedMemberAsync(factory, ApproverSubject, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentApprove)
            .ContinueWith(task => task.Result.Client, TaskScheduler.Default);

    private static async Task<AdjustmentDto> GetAdjustmentAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{WmsUrl()}/adjustments?pageSize=50", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdjustmentPageDto>(Ct))!.Items.Single(item => item.Id == id);
    }

    /// <summary>El conteo de 5 contra los 8 del sistema: el ajuste −3 queda <c>Proposed</c> y el saldo sigue en 8.</summary>
    private static async Task<(Shortage Shortage, Guid AdjustmentId, Guid VarianceId, HttpClient Counter)> ArrangeCountedAsync(
        PostgreSqlContainer database, QepApiFactory factory)
    {
        var shortage = await ArrangeShortageAsync(database, factory);
        var (_, counter) = await SeedCounterAsync(factory);
        await CountAsync(counter, shortage, 5);
        var adjustmentId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.adjustments ORDER BY number LIMIT 1");
        var varianceId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.incidents WHERE type = 'CountVariance' ORDER BY number LIMIT 1");
        return (shortage, adjustmentId, varianceId, counter);
    }

    private static async Task CountAsync(HttpClient counter, Shortage shortage, decimal counted)
    {
        var count = await ReadAsync<TaskDto>(await NextRawAsync(counter, shortage.Setup.WarehouseId));
        var response = await CountRawAsync(
            counter, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, shortage.Setup.BinA, counted), NewKey());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Un confirm real de otro movimiento de 1 unidad desde A-01-02: es lo que llega tarde de un celular sin conexión y mueve el saldo.
    private static async Task ConfirmOneUnitAsync(Shortage shortage)
    {
        var s = shortage.Setup;
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, shortage.Destination, 1)]));
        var next = await ReadAsync<TaskDto>(await NextRawAsync(shortage.ReporterClient, s.WarehouseId));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TasksUrl()}/{next.Id}/lines/{next.Lines[0].Id}/confirm")
        {
            Content = JsonContent.Create(new
            {
                leaseToken = next.LeaseToken,
                fromLocationId = s.BinA,
                toLocationId = shortage.Destination,
                quantity = 1,
                occurredAt = DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        Assert.Equal(HttpStatusCode.OK, (await shortage.ReporterClient.SendAsync(request, Ct)).StatusCode);
    }

    private static Task<string> StatusOfTaskAsync(PostgreSqlContainer database, Guid id) =>
        ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", id));

    private static Task<long> CountTasksAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks WHERE type = 'Count'");

    [Fact]
    public async Task PostingAfterAConfirmMovedTheStockRejectsTheAdjustmentAndAsksForANewCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (shortage, adjustmentId, varianceId, counter) = await ArrangeCountedAsync(database, factory);
        using var _r = shortage.ReporterClient;
        using var _k = counter;
        using var approver = await ApproverAsync(factory);
        var s = shortage.Setup;
        var proposed = await GetAdjustmentAsync(s.Manager, adjustmentId);
        var approved = await AdjustmentTestSupport.ActionRawAsync(approver, adjustmentId, "approve", new { version = proposed.Version });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var version = (await ReadAsync<AdjustmentDto>(approved)).Version;
        var originalCountId = shortage.CountId;
        await ConfirmOneUnitAsync(shortage);
        Assert.Equal(7m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        var response = await AdjustmentTestSupport.PostRawAsync(approver, adjustmentId, version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.adjustment.count_stale", await CodeOfAsync(response));
        var body = await BodyAsync(response);
        Assert.Contains("había 8 y ahora hay 7", body, StringComparison.Ordinal);
        var rejected = await GetAdjustmentAsync(s.Manager, adjustmentId);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Contains("había 8 y ahora hay 7", rejected.RejectNote, StringComparison.Ordinal);
        Assert.Equal("Rejected", await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", varianceId)));
        // El saldo no se tocó: sólo el confirm movió una unidad, y el ajuste no escribió nada al ledger.
        Assert.Equal(7m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Adjustment'"));
        // Un conteo nuevo de la misma clave, listo, con la novedad CountVariance como origen.
        Assert.Equal(2L, await CountTasksAsync(database));
        var recount = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count' AND id <> @id", ("id", originalCountId));
        Assert.Equal("Ready", await StatusOfTaskAsync(database, recount));
        Assert.Equal(
            varianceId,
            await ScalarAsync<Guid>(database, "SELECT source_id FROM wms.tasks WHERE id = @id", ("id", recount)));
        // La novedad que el conteo verificó queda enlazada al conteo nuevo.
        Assert.Equal(
            recount,
            await ScalarAsync<Guid>(database, "SELECT verification_task_id FROM wms.incidents WHERE id = @id", ("id", shortage.IncidentId)));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.rejected", adjustmentId.ToString()));
        Assert.Equal(0, await CountAuditEntriesAsync(factory, "wms.adjustment.posted", adjustmentId.ToString()));
    }

    [Fact]
    public async Task ApprovingAfterTheStockMovedAlsoRejectsTheAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (shortage, adjustmentId, varianceId, counter) = await ArrangeCountedAsync(database, factory);
        using var _r = shortage.ReporterClient;
        using var _k = counter;
        using var approver = await ApproverAsync(factory);
        var proposed = await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId);
        await ConfirmOneUnitAsync(shortage);

        var response = await AdjustmentTestSupport.ActionRawAsync(approver, adjustmentId, "approve", new { version = proposed.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.adjustment.count_stale", await CodeOfAsync(response));
        Assert.Equal("Rejected", (await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId)).Status);
        Assert.Equal("Rejected", await ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", varianceId)));
        Assert.Equal(2L, await CountTasksAsync(database));
    }

    [Fact]
    public async Task WithTheStockUnchangedApprovingAndPostingStillWork()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (shortage, adjustmentId, _, counter) = await ArrangeCountedAsync(database, factory);
        using var _r = shortage.ReporterClient;
        using var _k = counter;
        using var approver = await ApproverAsync(factory);
        var proposed = await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId);

        var approved = await AdjustmentTestSupport.ActionRawAsync(approver, adjustmentId, "approve", new { version = proposed.Version });
        var posted = await AdjustmentTestSupport.PostRawAsync(approver, adjustmentId, (await ReadAsync<AdjustmentDto>(approved)).Version);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(5m, await OnHandAsync(database, shortage.Setup.BinA, shortage.Setup.VariantId, StockStatus.Available));
        Assert.Equal(1L, await CountTasksAsync(database));
    }

    [Fact]
    public async Task TwoPendingCountVariancesPostTheFirstAndRejectTheSecond()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (shortage, firstId, _, counter) = await ArrangeCountedAsync(database, factory);
        using var _r = shortage.ReporterClient;
        using var _c = counter;
        var s = shortage.Setup;
        // Un segundo faltante sobre la misma clave (nada encontrado: el saldo sigue en 8) y un segundo conteo de 5: otro −3 pendiente.
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, shortage.Destination, 1)]));
        var next = await ReadAsync<TaskDto>(await NextRawAsync(shortage.ReporterClient, s.WarehouseId));
        var shorted = await ShortRawAsync(
            shortage.ReporterClient, next.Id, next.Lines[0].Id, ShortBody(next.LeaseToken!.Value, 0), NewKey());
        Assert.Equal(HttpStatusCode.OK, shorted.StatusCode);
        await CountAsync(counter, shortage, 5);
        var secondId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.adjustments ORDER BY number OFFSET 1 LIMIT 1");
        using var approver = await ApproverAsync(factory);
        var first = await AdjustmentTestSupport.ActionRawAsync(approver, firstId, "approve", new { version = (await GetAdjustmentAsync(s.Manager, firstId)).Version });
        var second = await AdjustmentTestSupport.ActionRawAsync(approver, secondId, "approve", new { version = (await GetAdjustmentAsync(s.Manager, secondId)).Version });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var postedFirst = await AdjustmentTestSupport.PostRawAsync(approver, firstId, (await ReadAsync<AdjustmentDto>(first)).Version);
        var postedSecond = await AdjustmentTestSupport.PostRawAsync(approver, secondId, (await ReadAsync<AdjustmentDto>(second)).Version);

        Assert.Equal(HttpStatusCode.OK, postedFirst.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, postedSecond.StatusCode);
        Assert.Equal("wms.adjustment.count_stale", await CodeOfAsync(postedSecond));
        Assert.Equal("Rejected", (await GetAdjustmentAsync(s.Manager, secondId)).Status);
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task TheNewCountIsTheOpenOneWhenTheKeyAlreadyHasOne()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (shortage, adjustmentId, _, counter) = await ArrangeCountedAsync(database, factory);
        using var _r = shortage.ReporterClient;
        using var _k = counter;
        var s = shortage.Setup;
        // Otro faltante sobre la misma clave deja un Count abierto (y mueve una unidad: el saldo ya no es el del conteo).
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, shortage.Destination, 2)]));
        var next = await ReadAsync<TaskDto>(await NextRawAsync(shortage.ReporterClient, s.WarehouseId));
        var shorted = await ShortRawAsync(
            shortage.ReporterClient, next.Id, next.Lines[0].Id, ShortBody(next.LeaseToken!.Value, 1), NewKey());
        Assert.Equal(HttpStatusCode.OK, shorted.StatusCode);
        Assert.Equal(2L, await CountTasksAsync(database));
        var open = await ScalarAsync<Guid>(
            database, "SELECT id FROM wms.tasks WHERE type = 'Count' AND id <> @id", ("id", shortage.CountId));
        using var approver = await ApproverAsync(factory);

        var response = await AdjustmentTestSupport.ActionRawAsync(
            approver, adjustmentId, "approve", new { version = (await GetAdjustmentAsync(s.Manager, adjustmentId)).Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.adjustment.count_stale", await CodeOfAsync(response));
        // Ningún Count nuevo: se enlazó al abierto, y la novedad del primer conteo también.
        Assert.Equal(2L, await CountTasksAsync(database));
        Assert.Equal(
            open,
            await ScalarAsync<Guid>(database, "SELECT verification_task_id FROM wms.incidents WHERE id = @id", ("id", shortage.IncidentId)));
    }
}
