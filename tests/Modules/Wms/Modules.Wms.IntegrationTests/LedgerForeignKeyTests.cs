using System.Net;
using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Spec §11.2: las FKs internas son ON DELETE RESTRICT; una ubicación con movimientos no se borra, se desactiva.</summary>
public sealed class LedgerForeignKeyTests
{
    [Fact]
    public async Task ALocationWithMovementsButNoBalanceCannotBeDeletedAtTheDatabase()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        await InsertRawMovementAsync(database, anchors, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "DELETE FROM wms.locations WHERE id = @id", ("id", anchors.FromLocationId)));

        Assert.Equal("FK_stock_movements_from_location", exception.ConstraintName);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
    }

    [Fact]
    public async Task AMovementWithoutARealLocationOrOwnerIsRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRawMovementAsync(database, anchors with { OwnerId = Guid.CreateVersion7() }, DateTimeOffset.UtcNow));

        Assert.Equal("FK_stock_movements_owner", exception.ConstraintName);
    }

    [Fact]
    public async Task DeletingThroughTheApiALocationWithMovementsAnswers422HasStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var anchors = await SeedMovementAnchorsAsync(factory, database);
        await InsertRawMovementAsync(database, anchors, DateTimeOffset.UtcNow);
        using var manager = CreateManager(factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{WmsUrl()}/warehouses/{anchors.WarehouseId}/locations/{anchors.ToLocationId}");

        var response = await manager.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.has_stock", await CodeOfAsync(response));
    }
}
