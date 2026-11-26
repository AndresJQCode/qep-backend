using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>Los ajustes manuales (RF-023) contra el esquema real: proponer, aprobar, contabilizar por el ledger y rechazar.</summary>
public sealed class AdjustmentApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProposingWithAutoApproveAndThePermissionReturnsApproved()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var adjustment = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)], autoApprove: true));

        Assert.Equal("Approved", adjustment.Status);
        Assert.Equal(2, adjustment.Version);
        Assert.Matches(@"^ADJ-\d{8}-0001$", adjustment.Number);
        Assert.Equal("A-01-02", adjustment.Lines.Single().LocationCode);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.proposed", adjustment.Id.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.approved", adjustment.Id.ToString()));
    }

    [Fact]
    public async Task ProposingWithAutoApproveWithoutThePermissionStaysProposed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        using var proposer = CreateClient(factory, SubjectId, TenantId, WmsPermissions.AdjustmentPropose, WmsPermissions.DocumentRead);

        var adjustment = await ProposeAsync(proposer, Body(s, [Line(s.BinA, s.VariantId, 5)], autoApprove: true));

        Assert.Equal("Proposed", adjustment.Status);
        Assert.Equal(0, await CountAuditEntriesAsync(factory, "wms.adjustment.approved", adjustment.Id.ToString()));
    }

    [Fact]
    public async Task PostingMovesStockAndPublishesTheEvent()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var posted = await PostedAsync(s.Manager, s, s.BinA, 10);

        Assert.Equal("Posted", posted.Status);
        Assert.NotNull(posted.BatchId);
        Assert.Equal(10m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(-10m, await OnHandAsync(database, s.Adjustment, s.VariantId, StockStatus.Available));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.posted", posted.Id.ToString()));

        var payload = await ScalarAsync<string>(
            database,
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = 'wms.adjustment.posted.v1'");
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(posted.Id, document.RootElement.GetProperty("adjustmentId").GetGuid());
        Assert.Equal(posted.BatchId, document.RootElement.GetProperty("batchId").GetGuid());
        Assert.Equal(1, document.RootElement.GetProperty("lineCount").GetInt32());
        Assert.Equal(s.OwnerId, document.RootElement.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task PostingAProposedAdjustmentIs422InvalidTransition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var proposed = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)]));

        var response = await PostRawAsync(s.Manager, proposed.Id, proposed.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("wms.document.invalid_transition", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Proposed", problem.RootElement.GetProperty("from").GetString());
        Assert.Equal("post", problem.RootElement.GetProperty("action").GetString());
        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 5);

        var response = await PostRawAsync(s.Manager, approved.Id, approved.Version - 1);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ApprovingAndRejectingFollowTheStateMachine()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var proposed = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)]));

        var approve = await ActionRawAsync(s.Manager, proposed.Id, "approve", new { version = proposed.Version });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = (await approve.Content.ReadFromJsonAsync<AdjustmentDto>(Ct))!;
        Assert.Equal("Approved", approved.Status);

        var again = await ActionRawAsync(s.Manager, proposed.Id, "approve", new { version = approved.Version });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);

        var withoutNote = await ActionRawAsync(s.Manager, proposed.Id, "reject", new { version = approved.Version, note = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutNote.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(withoutNote));

        var reject = await ActionRawAsync(s.Manager, proposed.Id, "reject", new { version = approved.Version, note = "Conteo repetido" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var rejected = (await reject.Content.ReadFromJsonAsync<AdjustmentDto>(Ct))!;
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Conteo repetido", rejected.RejectNote);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.adjustment.rejected", proposed.Id.ToString()));

        var post = await PostRawAsync(s.Manager, proposed.Id, rejected.Version);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task ANewLotIsCreatedOnProposeAndExpiryIsRequiredWhenTheProfileTracksIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot", tracksExpiry: true);

        var withoutExpiry = await ProposeRawAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5, lotCode: "L-1")]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutExpiry.StatusCode);
        Assert.Equal("wms.lot.expiry_required", await CodeOfAsync(withoutExpiry));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.lots"));

        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6));
        var adjustment = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5, lotCode: "L-1", expirationDate: expiry)], autoApprove: true));

        Assert.Equal("L-1", adjustment.Lines.Single().LotCode);
        Assert.NotNull(adjustment.Lines.Single().LotId);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.lots WHERE code = 'L-1'"));

        // El lote existente se reutiliza: la segunda propuesta no crea otro.
        await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 2, lotCode: "L-1")]));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.lots WHERE code = 'L-1'"));

        var posted = await PostRawAsync(s.Manager, adjustment.Id, adjustment.Version);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available, adjustment.Lines.Single().LotId));
    }

    [Fact]
    public async Task AnOccurredAtOlderThan30DaysIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var tooOld = await ProposeRawAsync(
            s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)], occurredAt: DateTimeOffset.UtcNow.AddDays(-31)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooOld.StatusCode);
        Assert.Equal("wms.movement.occurred_at_out_of_range", await CodeOfAsync(tooOld));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));

        var physical = DateTimeOffset.UtcNow.AddDays(-29);
        var adjustment = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)], autoApprove: true, occurredAt: physical));
        var posted = await PostRawAsync(s.Manager, adjustment.Id, adjustment.Version);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var written = await ScalarAsync<DateTime>(
            database, "SELECT occurred_at FROM wms.stock_movements WHERE document_id = @id", ("id", adjustment.Id));
        Assert.True(Math.Abs((new DateTimeOffset(written, TimeSpan.Zero) - physical).TotalSeconds) < 1);
    }

    [Fact]
    public async Task AProposalWithoutOwnerUsesTheSelfOwnerProvisionedOnTheFly()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var warehouse = await CreateWarehouseAsync(manager, "BOG");
        var bin = await CreateBinAsync(manager, warehouse.Id, "A", "A-01-02");
        var variantId = await CreateVariantAsync(factory, "V1", null, [12]);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.owners"));

        var response = await ProposeRawAsync(
            manager,
            new { warehouseId = warehouse.Id, reason = "Found", lines = new[] { Line(bin.Id, variantId, 3) }, autoApprove = false });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var adjustment = (await response.Content.ReadFromJsonAsync<AdjustmentDto>(Ct))!;
        var self = await ScalarAsync<Guid>(database, "SELECT id FROM wms.owners WHERE kind = 'Self'");
        Assert.Equal(self, adjustment.OwnerId);
    }

    [Fact]
    public async Task ListingFiltersByStatusAndWarehouse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var proposed = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 1)]));
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 2);

        var all = await s.Manager.GetFromJsonAsync<AdjustmentPageDto>($"{WmsUrl()}/adjustments", Ct);
        var onlyApproved = await s.Manager.GetFromJsonAsync<AdjustmentPageDto>($"{WmsUrl()}/adjustments?status=Approved", Ct);
        var otherWarehouse = await s.Manager.GetFromJsonAsync<AdjustmentPageDto>($"{WmsUrl()}/adjustments?warehouseId={Guid.NewGuid()}", Ct);
        var invalid = await s.Manager.GetAsync($"{WmsUrl()}/adjustments?status=Done", Ct);

        Assert.Equal(2, all!.TotalCount);
        Assert.Equal(approved.Id, all.Items[0].Id);
        Assert.Equal(proposed.Id, all.Items[1].Id);
        Assert.Equal("BOG", all.Items[0].WarehouseCode);
        Assert.Equal([approved.Id], onlyApproved!.Items.Select(item => item.Id));
        Assert.Empty(otherWarehouse!.Items);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task ARequestWithoutThePermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        using var reader = CreateReader(factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 1);

        var propose = await ProposeRawAsync(reader, Body(s, [Line(s.BinA, s.VariantId, 1)]));
        var post = await PostRawAsync(reader, approved.Id, approved.Version);

        Assert.Equal(HttpStatusCode.Forbidden, propose.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Theory]
    [InlineData("ADJUSTMENT")]
    [InlineData("TRANSIT")]
    [InlineData("SUPPLIER")]
    public async Task ALineOnAVirtualLocationIs422(string virtualCode)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var virtualId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.locations WHERE code = @code", ("code", virtualCode));

        var response = await ProposeRawAsync(s.Manager, Body(s, [Line(virtualId, s.VariantId, 5)]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }
}
