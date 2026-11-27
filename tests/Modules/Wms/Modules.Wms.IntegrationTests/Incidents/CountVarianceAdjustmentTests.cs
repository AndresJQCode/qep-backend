using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskCountApiTests;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>El ajuste <c>CountVariance</c> que propone un conteo (spec 2026-11-26 §8.4 reglas 3 a 6, DA-47): quien declaró el faltante
/// o contó no lo aprueba, el ajuste ya contabilizado resuelve la novedad y el rechazado la rechaza. Parte del conteo de 5 contra 8:
/// el ajuste es −3 sobre A-01-02.</summary>
public sealed class CountVarianceAdjustmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Variance(Shortage Shortage, Guid Counter, Guid AdjustmentId, Guid VarianceId, AdjustmentDto Adjustment);

    private static async Task<Variance> ArrangeVarianceAsync(PostgreSqlContainer database, QepApiFactory factory)
    {
        var shortage = await ArrangeShortageAsync(database, factory);
        var (counter, client) = await SeedCounterAsync(factory);
        using var _c = client;
        var count = await ReadAsync<Modules.Wms.Application.TaskDto>(await NextRawAsync(client, shortage.Setup.WarehouseId));
        var response = await CountRawAsync(
            client, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, shortage.Setup.BinA, 5), NewKey());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var adjustmentId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.adjustments");
        var varianceId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.incidents WHERE type = 'CountVariance'");
        var adjustment = await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId);
        return new Variance(shortage, counter, adjustmentId, varianceId, adjustment);
    }

    // No hay GET de un ajuste: se lee de la lista.
    private static async Task<AdjustmentDto> GetAdjustmentAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{WmsUrl()}/adjustments?pageSize=50", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<AdjustmentPageDto>(response)).Items.Single(item => item.Id == id);
    }

    // Un aprobador con una membresía propia y sólo los permisos de aprobar y leer.
    private static async Task<HttpClient> ApproverAsync(QepApiFactory factory, string subject) =>
        (await SeedMemberAsync(factory, subject, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentApprove)).Client;

    private static Task<string> IncidentStatusAsync(PostgreSqlContainer database, Guid id) =>
        ScalarAsync<string>(database, "SELECT status FROM wms.incidents WHERE id = @id", ("id", id));

    [Fact]
    public async Task TheReporterOfTheShortCannotApproveTheAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var v = await ArrangeVarianceAsync(database, factory);
        using var _r = v.Shortage.ReporterClient;
        // El reportante recibe el permiso de aprobar, y aun así no puede aprobar el ajuste que su faltante provocó.
        using var reporter = CreateClient(
            factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentApprove);

        var response = await AdjustmentTestSupport.ActionRawAsync(reporter, v.AdjustmentId, "approve", new { version = v.Adjustment.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal("Proposed", (await GetAdjustmentAsync(v.Shortage.Setup.Manager, v.AdjustmentId)).Status);
    }

    [Fact]
    public async Task TheReporterWhoseShortWasAcceptedByHandBeforeTheCountClosesCannotApproveIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        // El supervisor acepta el faltante a mano antes de que alguien cuente: la novedad ya no está abierta, pero sigue enlazada al Count.
        var accepted = await shortage.Setup.Manager.PostAsJsonAsync(
            $"{WmsUrl()}/incidents/{shortage.IncidentId}/resolve", new { version = 1, resolution = "Accept" }, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var (_, counterClient) = await SeedCounterAsync(factory);
        using var _c = counterClient;
        var count = await ReadAsync<Modules.Wms.Application.TaskDto>(await NextRawAsync(counterClient, shortage.Setup.WarehouseId));
        var counted = await CountRawAsync(
            counterClient, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, shortage.Setup.BinA, 5), NewKey());
        Assert.Equal(HttpStatusCode.OK, counted.StatusCode);
        var adjustmentId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.adjustments");
        var adjustment = await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId);
        using var reporter = CreateClient(
            factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentApprove);

        var response = await AdjustmentTestSupport.ActionRawAsync(reporter, adjustmentId, "approve", new { version = adjustment.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal("Proposed", (await GetAdjustmentAsync(shortage.Setup.Manager, adjustmentId)).Status);
    }

    [Fact]
    public async Task TheCounterCannotApproveIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var v = await ArrangeVarianceAsync(database, factory);
        using var _r = v.Shortage.ReporterClient;
        using var counter = CreateClient(
            factory, SecondOperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentApprove);

        var response = await AdjustmentTestSupport.ActionRawAsync(counter, v.AdjustmentId, "approve", new { version = v.Adjustment.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal("Proposed", (await GetAdjustmentAsync(v.Shortage.Setup.Manager, v.AdjustmentId)).Status);
    }

    [Fact]
    public async Task AnotherApproverApprovesAndPostingResolvesTheCountVariance()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var v = await ArrangeVarianceAsync(database, factory);
        using var _r = v.Shortage.ReporterClient;
        using var approver = await ApproverAsync(factory, "01900000-0000-7000-8000-000000000006");

        var approved = await AdjustmentTestSupport.ActionRawAsync(approver, v.AdjustmentId, "approve", new { version = v.Adjustment.Version });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var version = (await ReadAsync<AdjustmentDto>(approved)).Version;
        var posted = await PostRawAsync(approver, v.AdjustmentId, version);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var batch = (await ReadAsync<AdjustmentDto>(posted)).BatchId;
        Assert.NotNull(batch);
        // 8 en el sistema menos el −3 del conteo.
        Assert.Equal(5m, await OnHandAsync(database, v.Shortage.Setup.BinA, v.Shortage.Setup.VariantId, StockStatus.Available));
        Assert.Equal("Resolved", await IncidentStatusAsync(database, v.VarianceId));
        Assert.Equal("Accept", await ScalarAsync<string>(database, "SELECT resolution FROM wms.incidents WHERE id = @id", ("id", v.VarianceId)));
        Assert.Equal(batch, await ScalarAsync<Guid>(database, "SELECT batch_id FROM wms.incidents WHERE id = @id", ("id", v.VarianceId)));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.approved", v.AdjustmentId.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.posted", v.AdjustmentId.ToString()));
    }

    [Fact]
    public async Task RejectingTheAdjustmentRejectsTheCountVariance()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var v = await ArrangeVarianceAsync(database, factory);
        using var _r = v.Shortage.ReporterClient;
        using var approver = await ApproverAsync(factory, "01900000-0000-7000-8000-000000000006");

        var rejected = await AdjustmentTestSupport.ActionRawAsync(
            approver, v.AdjustmentId, "reject", new { version = v.Adjustment.Version, note = "Se recontó y está bien" });

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("Rejected", await IncidentStatusAsync(database, v.VarianceId));
        Assert.Equal(
            "Se recontó y está bien",
            await ScalarAsync<string>(database, "SELECT resolution_note FROM wms.incidents WHERE id = @id", ("id", v.VarianceId)));
        Assert.Equal(8m, await OnHandAsync(database, v.Shortage.Setup.BinA, v.Shortage.Setup.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ACountVarianceCannotBeResolvedOrRejectedByHand()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var v = await ArrangeVarianceAsync(database, factory);
        using var _r = v.Shortage.ReporterClient;
        var manager = v.Shortage.Setup.Manager;

        var resolve = await manager.PostAsJsonAsync(
            $"{WmsUrl()}/incidents/{v.VarianceId}/resolve", new { version = 1, resolution = "Accept" }, Ct);
        var reject = await manager.PostAsJsonAsync(
            $"{WmsUrl()}/incidents/{v.VarianceId}/reject", new { version = 1, note = "no" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resolve.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(resolve));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reject.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(reject));
        Assert.Equal("Open", await IncidentStatusAsync(database, v.VarianceId));
    }

    [Fact]
    public async Task AnAdjustmentWithoutIncidentIsApprovedAsBefore()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        // Quien propone y quien aprueba es el mismo sujeto: sin novedad detrás no se pide membresía ni separación.
        using var both = CreateClient(
            factory, SubjectId, TenantId, WmsPermissions.DocumentRead, WmsPermissions.AdjustmentPropose, WmsPermissions.AdjustmentApprove);
        var proposed = await ProposeAsync(both, AdjustmentTestSupport.Body(s, [AdjustmentTestSupport.Line(s.BinA, s.VariantId, 5)]));

        var approved = await AdjustmentTestSupport.ActionRawAsync(both, proposed.Id, "approve", new { version = proposed.Version });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Approved", (await ReadAsync<AdjustmentDto>(approved)).Status);
    }
}
