using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>El reverso de un movimiento (RF-025): un compensatorio que apunta al original, comparte su lote y nunca se aplica dos
/// veces.</summary>
public sealed class ReverseMovementApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> ReverseRawAsync(HttpClient client, Guid movementId, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/stock/movements/{movementId}/reverse")
        {
            Content = JsonContent.Create(body ?? new { note = "Error de digitación" }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<(AdjustmentDto Adjustment, Guid MovementId)> PostedMovementAsync(
        Modules.Wms.IntegrationTests.WmsApiHarness.QepApiFactory factory, Testcontainers.PostgreSql.PostgreSqlContainer database, LedgerSetup s, decimal delta)
    {
        var posted = await PostedAsync(s.Manager, s, s.BinA, delta);
        var movementId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.stock_movements WHERE document_id = @id", ("id", posted.Id));
        return (posted, movementId);
    }

    [Fact]
    public async Task ReversingAMovementRestoresTheBalancesAndSharesTheBatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (posted, movementId) = await PostedMovementAsync(factory, database, s, 10);
        Assert.Equal(10m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        var response = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var reversal = (await response.Content.ReadFromJsonAsync<MovementDto>(Ct))!;
        Assert.Equal("Reversal", reversal.Reason);
        Assert.Equal(movementId, reversal.ReversesMovementId);
        Assert.Equal(posted.BatchId, reversal.BatchId);
        Assert.Equal(10m, reversal.Quantity);
        Assert.Equal("A-01-02", reversal.From.LocationCode);
        Assert.Equal("ADJUSTMENT", reversal.To.LocationCode);
        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(0m, await OnHandAsync(database, s.Adjustment, s.VariantId, StockStatus.Available));

        // S4: la referencia blanda al original lleva también su fecha, para ubicar su partición.
        Assert.True(await ScalarAsync<bool>(
            database,
            """
            SELECT r.reverses_occurred_at = o.occurred_at
            FROM wms.stock_movements r JOIN wms.stock_movements o ON o.id = r.reverses_movement_id
            WHERE r.id = @reversal
            """,
            ("reversal", reversal.Id)));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.movement.reversed", reversal.Id.ToString()));
        var note = await ScalarAsync<string>(database, "SELECT override_reason FROM wms.stock_movements WHERE id = @id", ("id", reversal.Id));
        Assert.Equal("Error de digitación", note);
    }

    // D-12: el reverso rearma las dos claves desde el movimiento guardado, unidad incluida. Sin ella compensaría el saldo suelto.
    [Fact]
    public async Task TheReversalRestoresTheHandlingUnitOfEachSide()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var unit = Guid.CreateVersion7();
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, StockStatus.Available, s.Receiving, StockStatus.Received, 5, MovementReason.Receipt)));
        await StockAsync(factory, s, s.BinA, 7);
        var received = new StockKey(s.OwnerId, s.Receiving, s.VariantId, null, StockStatus.Received);
        var inUnit = new StockKey(s.OwnerId, s.BinA, s.VariantId, null, StockStatus.Available, unit);
        var loose = inUnit with { HandlingUnitId = null };
        var movementId = await SeedUnitMovementAsync(database, s, received, inUnit, 5);

        var response = await ReverseRawAsync(s.Manager, movementId, Guid.NewGuid().ToString());

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var reversal = (await response.Content.ReadFromJsonAsync<MovementDto>(Ct))!;
        Assert.Equal(0m, await OnHandAsync(database, inUnit));
        Assert.Equal(7m, await OnHandAsync(database, loose));
        Assert.Equal(5m, await OnHandAsync(database, received));
        Assert.Equal(
            unit,
            await ScalarAsync<Guid>(database, "SELECT from_handling_unit_id FROM wms.stock_movements WHERE id = @id", ("id", reversal.Id)));
        Assert.Null(await ScalarAsync<Guid?>(database, "SELECT to_handling_unit_id FROM wms.stock_movements WHERE id = @id", ("id", reversal.Id)));
    }

    // Inventario básico §5.6: lo que asentó un pedido, una venta o una compra se deshace desde el documento. Una reversa manual
    // seguida de un «deshacer preparado» devolvería dos veces.
    [Theory]
    [InlineData("Order")]
    [InlineData("PosSale")]
    [InlineData("PurchaseOrder")]
    public async Task ReversingAMovementOfAnOrderIsRejected(string documentType)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        var customer = (await ListLocationsAsync(s.Manager, s.WarehouseId)).Single(location => location.Code == "CUSTOMER").Id;
        var posting = await PostAsync(factory, Batch(s, new LedgerEntry(
            new StockKey(s.OwnerId, s.BinA, s.VariantId, null, StockStatus.Available),
            new StockKey(s.OwnerId, customer, s.VariantId, null, StockStatus.Available),
            3,
            null,
            MovementReason.Ship,
            new LedgerDocument(documentType, Guid.CreateVersion7(), Guid.CreateVersion7()),
            DateTimeOffset.UtcNow)));

        var response = await ReverseRawAsync(s.Manager, posting.MovementIds.Single(), Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.movement.reverse_from_document", await CodeOfAsync(response));
        Assert.Equal(7m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ReversingTwiceIs422AlreadyReversed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);
        var first = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await ReverseRawAsync(s.Manager, movementId, "88e2a550-cdbf-59c7-9914-aaf3f15099aa");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("wms.movement.already_reversed", await CodeOfAsync(second));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE reverses_movement_id = @id", ("id", movementId)));
    }

    [Fact]
    public async Task ReversingAReversalIs422AlreadyReversed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);
        var first = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");
        var reversal = (await first.Content.ReadFromJsonAsync<MovementDto>(Ct))!;

        var response = await ReverseRawAsync(s.Manager, reversal.Id, "88e2a550-cdbf-59c7-9914-aaf3f15099aa");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.movement.already_reversed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ReversingWithoutTheHeaderIs428()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);

        var response = await ReverseRawAsync(s.Manager, movementId, key: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("wms.idempotency_key_required", await CodeOfAsync(response));
        Assert.Equal(10m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ReversingWithTheSameKeyReplays()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);

        var first = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");
        var second = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstMovement = (await first.Content.ReadFromJsonAsync<MovementDto>(Ct))!;
        var secondMovement = (await second.Content.ReadFromJsonAsync<MovementDto>(Ct))!;
        Assert.Equal(firstMovement.Id, secondMovement.Id);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE reverses_movement_id = @id", ("id", movementId)));
        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ReversingAMovementWhoseStockWasConsumedIs422Insufficient()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);
        await PostedAsync(s.Manager, s, s.BinA, -8);

        var response = await ReverseRawAsync(s.Manager, movementId, "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("wms.stock.insufficient", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(2m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts WHERE idempotency_key = 'c7ef0e43-20e4-5fd1-a849-5c110e73ea69'"));
    }

    [Fact]
    public async Task AnUnknownMovementIs404AndTheReadOnlyRoleIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);
        using var reader = CreateReader(factory);

        var missing = await ReverseRawAsync(s.Manager, Guid.CreateVersion7(), "c7ef0e43-20e4-5fd1-a849-5c110e73ea69");
        var denied = await ReverseRawAsync(reader, movementId, "88e2a550-cdbf-59c7-9914-aaf3f15099aa");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("wms.movement.not_found", await CodeOfAsync(missing));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ReversingDoesNotRevalidateACaptureWhoseConversionWasRemoved()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var created = await s.Manager.PutAsJsonAsync(
            $"{WmsUrl()}/variant-profiles/{s.VariantId}",
            ProfileBody(new[] { new { code = "CAJA", factor = 12m, barcode = (string?)null } }, version: 0),
            Ct);
        created.EnsureSuccessStatusCode();
        var saved = JsonDocument.Parse(await BodyAsync(created)).RootElement.GetProperty("version").GetInt64();
        var adjustment = await ProposeAsync(
            s.Manager,
            Body(
                s,
                [new { locationId = s.BinA, variantId = s.VariantId, status = "Available", quantityDelta = 24m, capturedUom = "CAJA", capturedQty = 2m }],
                autoApprove: true));
        Assert.Equal(HttpStatusCode.OK, (await PostRawAsync(s.Manager, adjustment.Id, adjustment.Version)).StatusCode);
        var movementId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.stock_movements WHERE document_id = @id", ("id", adjustment.Id));
        var removed = await s.Manager.PutAsJsonAsync(
            $"{WmsUrl()}/variant-profiles/{s.VariantId}", ProfileBody(Array.Empty<object>(), saved), Ct);
        removed.EnsureSuccessStatusCode();

        var response = await ReverseRawAsync(s.Manager, movementId, "669d006e-514d-5a83-a3c1-e967ced3f57d");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        Assert.Equal(0m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    private static object ProfileBody(object conversions, long version) =>
        new
        {
            baseUom = "UND",
            baseUomPrecision = 0,
            tracking = "None",
            tracksExpiry = false,
            weightKg = 1.5m,
            storageClass = "FRIO",
            conversions,
            version,
        };
}
