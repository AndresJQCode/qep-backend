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

        var response = await ReverseRawAsync(s.Manager, movementId, "R-1");

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

    [Fact]
    public async Task ReversingTwiceIs422AlreadyReversed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, movementId) = await PostedMovementAsync(factory, database, s, 10);
        var first = await ReverseRawAsync(s.Manager, movementId, "R-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await ReverseRawAsync(s.Manager, movementId, "R-2");

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
        var first = await ReverseRawAsync(s.Manager, movementId, "R-1");
        var reversal = (await first.Content.ReadFromJsonAsync<MovementDto>(Ct))!;

        var response = await ReverseRawAsync(s.Manager, reversal.Id, "R-2");

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

        var first = await ReverseRawAsync(s.Manager, movementId, "R-1");
        var second = await ReverseRawAsync(s.Manager, movementId, "R-1");

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

        var response = await ReverseRawAsync(s.Manager, movementId, "R-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("wms.stock.insufficient", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(2m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts WHERE idempotency_key = 'R-1'"));
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

        var missing = await ReverseRawAsync(s.Manager, Guid.CreateVersion7(), "R-1");
        var denied = await ReverseRawAsync(reader, movementId, "R-2");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("wms.movement.not_found", await CodeOfAsync(missing));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
