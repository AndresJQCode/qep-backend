using System.Net;
using System.Text.Json;
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
