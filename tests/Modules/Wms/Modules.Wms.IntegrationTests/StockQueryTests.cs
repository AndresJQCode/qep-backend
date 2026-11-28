using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>Las lecturas de saldos e historia (RF-021, RF-022) contra el esquema real, sembradas posteando por el ledger.</summary>
public sealed class StockQueryTests
{
    private const StockStatus Available = StockStatus.Available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync($"{WmsUrl()}{path}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await BodyAsync(response));
    }

    [Fact]
    public async Task FindOnHandAsyncReadsTheExactKey()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        // Una variante sin control de lote y otra por lote: el ledger no admite las dos formas en la misma variante.
        var lotVariant = await CreateVariantAsync(factory, "V2", null, [12]);
        await SetTrackingAsync(s.Manager, lotVariant, "Lot");
        var lot = await CreateLotAsync(factory, s, lotVariant, "L-1");
        await StockAsync(factory, s, s.BinA, 10);
        await StockAsync(factory, s, s.BinA, 4, lot, lotVariant);
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, StockStatus.Quarantine, 20)));
        await using var scope = factory.Services.CreateAsyncScope();
        var stock = scope.ServiceProvider.GetRequiredService<IStockReadQueries>();

        StockKey Key(Guid variant, Guid? withLot, StockStatus status) => new(s.OwnerId, s.BinA, variant, withLot, status);

        Assert.Equal(10m, await stock.FindOnHandAsync(s.TenantId, Key(s.VariantId, null, Available), Ct));
        Assert.Equal(20m, await stock.FindOnHandAsync(s.TenantId, Key(s.VariantId, null, StockStatus.Quarantine), Ct));
        Assert.Equal(0m, await stock.FindOnHandAsync(s.TenantId, Key(s.VariantId, null, StockStatus.Blocked), Ct));
        Assert.Equal(0m, await stock.FindOnHandAsync(Guid.CreateVersion7(), Key(s.VariantId, null, Available), Ct));
        Assert.Equal(4m, await stock.FindOnHandAsync(s.TenantId, Key(lotVariant, lot, Available), Ct));
        // Lote nulo es «sin lote», no «cualquiera»: los 4 del lote no se cuentan como sueltos.
        Assert.Equal(0m, await stock.FindOnHandAsync(s.TenantId, Key(lotVariant, null, Available), Ct));
        Assert.Equal(0m, await stock.FindOnHandAsync(s.TenantId, Key(lotVariant, Guid.CreateVersion7(), Available), Ct));
    }

    [Fact]
    public async Task TheStockPageCarriesTotalsOfTheWholeFilterNotOfThePage()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 10)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, StockStatus.Quarantine, 20)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, StockStatus.Received, 30, MovementReason.Receipt)));

        using var page = await GetJsonAsync(s.Manager, "/stock?pageSize=2");

        var root = page.RootElement;
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(60m, root.GetProperty("totals").GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task VirtualLocationsNeverReachTotalsNorSummary()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostAsync(factory, Batch(s, Entry(s, s.Adjustment, Available, s.BinA, Available, 10)));

        using var listed = await GetJsonAsync(s.Manager, "/stock");
        using var withVirtual = await GetJsonAsync(s.Manager, "/stock?includeVirtual=true");
        using var summary = await GetJsonAsync(s.Manager, "/stock/summary");

        Assert.Equal(1, listed.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(10m, listed.RootElement.GetProperty("totals").GetProperty("onHand").GetDecimal());
        Assert.Equal(2, withVirtual.RootElement.GetProperty("items").GetArrayLength());
        // Con las virtuales a la vista, totals sigue sin sumarlas: el -10 de ADJUSTMENT no se lleva el 10 del bin.
        Assert.Equal(10m, withVirtual.RootElement.GetProperty("totals").GetProperty("onHand").GetDecimal());
        Assert.Equal(10m, summary.RootElement.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task TheSummarySplitsReceivedBlockedAndInTransit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, StockStatus.Received, 5, MovementReason.Receipt)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, StockStatus.Quarantine, 2, MovementReason.Receipt)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 10)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Transit, StockStatus.InTransit, 3)));

        using var summary = await GetJsonAsync(s.Manager, "/stock/summary");

        var root = summary.RootElement;
        Assert.Equal(17m, root.GetProperty("onHand").GetDecimal());
        Assert.Equal(5m, root.GetProperty("received").GetDecimal());
        Assert.Equal(10m, root.GetProperty("available").GetDecimal());
        Assert.Equal(10m, root.GetProperty("pickable").GetDecimal());
        Assert.Equal(2m, root.GetProperty("blocked").GetDecimal());
        Assert.Equal(3m, root.GetProperty("inTransit").GetDecimal());
        Assert.Equal(0m, root.GetProperty("reserved").GetDecimal());
        Assert.Equal(0m, root.GetProperty("expected").GetDecimal());
    }

    [Fact]
    public async Task PickableFollowsAllowsPicking()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var reserve = await CreateBinAsync(s.Manager, s.WarehouseId, "B", "B-01-01", allowsPicking: false);
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, reserve.Id, Available, 7)));
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 10)));

        using var listed = await GetJsonAsync(s.Manager, $"/stock?locationId={reserve.Id}");
        using var summary = await GetJsonAsync(s.Manager, "/stock/summary");

        var row = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(7m, row.GetProperty("available").GetDecimal());
        Assert.Equal(0m, row.GetProperty("pickable").GetDecimal());
        Assert.Equal(10m, summary.RootElement.GetProperty("pickable").GetDecimal());
        Assert.Equal(17m, summary.RootElement.GetProperty("available").GetDecimal());
        // totals trae pickable (spec 24.3): lo que está en el bin sin picking no se puede tomar.
        using var all = await GetJsonAsync(s.Manager, "/stock");
        Assert.Equal(10m, all.RootElement.GetProperty("totals").GetProperty("pickable").GetDecimal());
        Assert.Equal(17m, all.RootElement.GetProperty("totals").GetProperty("available").GetDecimal());
    }

    [Fact]
    public async Task TheStockRowCarriesTheCatalogNamesAndTheBaseUom()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 4)));

        using var listed = await GetJsonAsync(s.Manager, $"/stock?warehouseId={s.WarehouseId}&variantId={s.VariantId}&search=A-01");

        var row = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("V1", row.GetProperty("variantCode").GetString());
        Assert.Equal("A-01-02", row.GetProperty("locationCode").GetString());
        Assert.Equal("BOG", row.GetProperty("warehouseCode").GetString());
        Assert.Equal("UND", row.GetProperty("baseUom").GetString());
        Assert.Equal(4m, row.GetProperty("onHand").GetDecimal());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("productName").GetString()));
    }

    [Fact]
    public async Task AnUnknownStatusIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await s.Manager.GetAsync($"{WmsUrl()}/stock?status=Nonsense", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.stock.status_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheMovementHistoryIsCursorPagedNewestFirstAndFilteredByLocation()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var other = await CreateBinAsync(s.Manager, s.WarehouseId, "B", "B-01-01");
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var expected = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var posting = await PostAsync(
                factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 1, occurredAt: start.AddMinutes(index))));
            expected.Add(posting.MovementIds.Single());
        }

        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, other.Id, Available, 1, occurredAt: start.AddMinutes(10))));
        expected.Reverse();

        using var first = await GetJsonAsync(s.Manager, $"/stock/movements?locationId={s.BinA}&limit=2");
        var firstCursor = first.RootElement.GetProperty("nextCursor").GetString();
        using var second = await GetJsonAsync(s.Manager, $"/stock/movements?locationId={s.BinA}&limit=2&cursor={firstCursor}");
        var secondCursor = second.RootElement.GetProperty("nextCursor").GetString();
        using var third = await GetJsonAsync(s.Manager, $"/stock/movements?locationId={s.BinA}&limit=2&cursor={secondCursor}");

        static List<Guid> IdsOf(JsonDocument page) =>
            [.. page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

        Assert.NotNull(firstCursor);
        Assert.NotNull(secondCursor);
        Assert.Equal(expected.Take(2), IdsOf(first));
        Assert.Equal(expected.Skip(2).Take(2), IdsOf(second));
        Assert.Equal(expected.Skip(4), IdsOf(third));
        Assert.Equal(JsonValueKind.Null, third.RootElement.GetProperty("nextCursor").ValueKind);

        var movement = first.RootElement.GetProperty("items")[0];
        Assert.Equal("SUPPLIER", movement.GetProperty("from").GetProperty("locationCode").GetString());
        Assert.Equal("A-01-02", movement.GetProperty("to").GetProperty("locationCode").GetString());
        Assert.Equal("UND", movement.GetProperty("baseUom").GetString());
        Assert.Equal("V1", movement.GetProperty("variantCode").GetString());
    }

    [Fact]
    public async Task MovementsSharingOccurredAtAreNeitherSkippedNorRepeatedAcrossPages()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var newest = DateTimeOffset.UtcNow.AddMinutes(-5);
        var shared = DateTimeOffset.UtcNow.AddMinutes(-10);
        // Las líneas de un ajuste comparten hora: el desempate lo da el id, y la frontera de página cae en medio del par.
        var batch = Batch(
            s,
            Entry(s, s.Supplier, Available, s.BinA, Available, 1, occurredAt: shared),
            Entry(s, s.Supplier, Available, s.BinA, Available, 2, occurredAt: shared),
            Entry(s, s.Supplier, Available, s.BinA, Available, 3, occurredAt: shared));
        var posting = await PostAsync(factory, batch);
        var latest = (await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.BinA, Available, 4, occurredAt: newest)))).MovementIds.Single();
        var expected = new List<Guid> { latest };
        expected.AddRange(posting.MovementIds.OrderByDescending(id => id));

        using var first = await GetJsonAsync(s.Manager, $"/stock/movements?locationId={s.BinA}&limit=2");
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        using var second = await GetJsonAsync(s.Manager, $"/stock/movements?locationId={s.BinA}&limit=2&cursor={cursor}");

        static List<Guid> IdsOf(JsonDocument page) =>
            [.. page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

        Assert.Equal(expected.Take(2), IdsOf(first));
        Assert.Equal(expected.Skip(2), IdsOf(second));
    }

    [Fact]
    public async Task AGarbageCursorIs422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await s.Manager.GetAsync($"{WmsUrl()}/stock/movements?cursor=zzz", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Cursor", out var cursorErrors));
        Assert.NotEqual(0, cursorErrors.GetArrayLength());
    }

    [Fact]
    public async Task AnotherTenantGetsForbiddenNeverNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var stranger = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        foreach (var path in new[] { "/stock", "/stock/summary", "/stock/movements" })
        {
            var response = await stranger.GetAsync($"{WmsUrl(TenantId)}{path}", Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", await CodeOfAsync(response));
        }
    }

    // Inventario básico §5.4: la reserva de demanda (de un pedido) es por bodega y variante, sin ubicación, y no toca el saldo físico.
    private static async Task<T> InDemandScopeAsync<T>(
        WmsApiHarness.QepApiFactory factory, Func<IDemandReservations, IWmsUnitOfWork, Task<T>> work)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var demands = scope.ServiceProvider.GetRequiredService<IDemandReservations>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        var result = await work(demands, unitOfWork);
        await unitOfWork.SaveChangesAsync(Ct);
        return result;
    }

    private static Task<bool> OpenDemandAsync(
        WmsApiHarness.QepApiFactory factory, LedgerSetup s, decimal quantity, Guid demandId, Guid? lineId = null) =>
        InDemandScopeAsync(factory, async (demands, _) =>
        {
            await demands.OpenAsync(
                new DemandLine(s.TenantId, s.OwnerId, s.VariantId, quantity, InventoryDocumentTypes.Order, demandId, lineId ?? Guid.CreateVersion7()),
                s.WarehouseId,
                Ct);
            return true;
        });

    [Fact]
    public async Task ADemandReservationWithoutBalanceAppearsInTheSummary()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        await OpenDemandAsync(factory, s, 2, Guid.CreateVersion7());

        using var summary = await GetJsonAsync(s.Manager, "/stock/summary");
        var root = summary.RootElement;
        Assert.Equal(0m, root.GetProperty("onHand").GetDecimal());
        Assert.Equal(2m, root.GetProperty("reserved").GetDecimal());
        // Sin recortar: disponible negativo es lo que dice que se vendió más de lo que hay.
        Assert.Equal(-2m, root.GetProperty("available").GetDecimal());
        Assert.Equal(0m, root.GetProperty("pickable").GetDecimal());
    }

    [Fact]
    public async Task TheListTotalsMatchTheSummary()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await OpenDemandAsync(factory, s, 3, Guid.CreateVersion7());
        await OpenDemandAsync(factory, s, 1.5m, Guid.CreateVersion7());
        var filter = $"warehouseId={s.WarehouseId}&variantId={s.VariantId}";

        using var summary = await GetJsonAsync(s.Manager, $"/stock/summary?{filter}");
        using var list = await GetJsonAsync(s.Manager, $"/stock?{filter}");

        var totals = list.RootElement.GetProperty("totals");
        Assert.Equal(4.5m, totals.GetProperty("reserved").GetDecimal());
        Assert.Equal(5.5m, totals.GetProperty("available").GetDecimal());
        Assert.Equal(10m, totals.GetProperty("onHand").GetDecimal());
        Assert.Equal(summary.RootElement.GetProperty("reserved").GetDecimal(), totals.GetProperty("reserved").GetDecimal());
        Assert.Equal(summary.RootElement.GetProperty("available").GetDecimal(), totals.GetProperty("available").GetDecimal());
        // Lo físico que se puede tomar no baja por una reserva de demanda: pickable puede superar a available.
        Assert.Equal(10m, totals.GetProperty("pickable").GetDecimal());
        Assert.Equal(10m, summary.RootElement.GetProperty("pickable").GetDecimal());
    }

    [Fact]
    public async Task RowsByLocationDoNotShowDemandReservations()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await OpenDemandAsync(factory, s, 3, Guid.CreateVersion7());

        using var list = await GetJsonAsync(s.Manager, "/stock");

        var row = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0m, row.GetProperty("reserved").GetDecimal());
        Assert.Equal(10m, row.GetProperty("available").GetDecimal());
        // Una búsqueda por ubicación o lote no puede incluir lo que no tiene ubicación.
        using var byLocation = await GetJsonAsync(s.Manager, $"/stock?locationId={s.BinA}");
        Assert.Equal(0m, byLocation.RootElement.GetProperty("totals").GetProperty("reserved").GetDecimal());
    }

    [Fact]
    public async Task OpeningADemandReservationEnqueuesABalanceChange()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await ExecuteAsync(database, "DELETE FROM wms.balance_change_queue");

        await OpenDemandAsync(factory, s, 2, Guid.CreateVersion7());

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                database,
                "SELECT count(*) FROM wms.balance_change_queue WHERE tenant_id = @t AND owner_id = @o AND warehouse_id = @w AND variant_id = @v",
                ("t", s.TenantId),
                ("o", s.OwnerId),
                ("w", s.WarehouseId),
                ("v", s.VariantId)));
        // Sin asiento: sólo la reserva produjo el aviso.
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements"));
    }

    [Fact]
    public async Task DemandReservationsCloseConsumeAndReopen()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var order = Guid.CreateVersion7();
        var lineA = Guid.CreateVersion7();
        var lineB = Guid.CreateVersion7();
        await OpenDemandAsync(factory, s, 2, order, lineA);
        await OpenDemandAsync(factory, s, 3, order, lineB);
        var reserved = () => ScalarAsync<decimal>(
            database,
            "SELECT coalesce(sum(quantity), 0) FROM wms.reservations WHERE demand_id = @d AND status = 'Open' AND location_id IS NULL",
            ("d", order));
        Assert.Equal(5m, await reserved());

        // Consumir devuelve lo que consumió: de ahí sale la reserva nueva si el asiento se deshace.
        var consumed = await InDemandScopeAsync(
            factory, (demands, _) => demands.ConsumeAllAsync(s.TenantId, InventoryDocumentTypes.Order, order, Ct));

        Assert.Equal([2m, 3m], consumed.Select(reservation => reservation.Quantity).Order().ToArray());
        Assert.All(consumed, reservation => Assert.Equal(ReservationStatus.Consumed, reservation.Status));
        Assert.Equal(0m, await reserved());
        Assert.Equal(2L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.reservations WHERE demand_id = @d AND status = 'Consumed'", ("d", order)));

        await InDemandScopeAsync(factory, async (demands, _) =>
        {
            await demands.ReopenAsync(consumed, Ct);
            return true;
        });
        Assert.Equal(5m, await reserved());

        // Cerrar libera las abiertas; y una línea que se abre de nuevo (edición) no choca con la cerrada.
        await InDemandScopeAsync(factory, async (demands, _) =>
        {
            await demands.CloseAllAsync(s.TenantId, InventoryDocumentTypes.Order, order, Ct);
            await demands.OpenAsync(
                new DemandLine(s.TenantId, s.OwnerId, s.VariantId, 7, InventoryDocumentTypes.Order, order, lineA), s.WarehouseId, Ct);
            return true;
        });
        Assert.Equal(7m, await reserved());
        Assert.Equal(2L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.reservations WHERE demand_id = @d AND status = 'Released'", ("d", order)));
    }

    [Fact]
    public async Task AReaderWithoutStockReadGetsForbidden()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{WmsUrl()}/stock", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
