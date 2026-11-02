using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>La carga inicial por Excel (RF-024): un preview que responde 200 con los problemas por fila, y un commit que contabiliza
/// todo en un solo batch de ledger o no contabiliza nada.</summary>
public sealed class InitialBalanceImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record SheetRow(
        string? Code, string? Location, decimal? Quantity = 5m, string? Lot = null, DateOnly? Expiration = null, string? Uom = null);

    private static byte[] Workbook(IEnumerable<SheetRow> rows, string[]? headers = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Saldos");
        var names = headers ?? ["Code", "Location", "Lot", "Expiration", "Quantity", "Uom"];
        for (var column = 0; column < names.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = names[column];
        }

        var row = 2;
        foreach (var item in rows)
        {
            if (item.Code is not null)
            {
                sheet.Cell(row, 1).Value = item.Code;
            }

            if (item.Location is not null)
            {
                sheet.Cell(row, 2).Value = item.Location;
            }

            if (item.Lot is not null)
            {
                sheet.Cell(row, 3).Value = item.Lot;
            }

            if (item.Expiration is { } expiration)
            {
                sheet.Cell(row, 4).Value = expiration.ToDateTime(TimeOnly.MinValue);
            }

            if (item.Quantity is { } quantity)
            {
                sheet.Cell(row, 5).Value = quantity;
            }

            if (item.Uom is not null)
            {
                sheet.Cell(row, 6).Value = item.Uom;
            }

            row++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static Task<HttpResponseMessage> PreviewRawAsync(HttpClient client, Guid warehouseId, byte[] file, Guid? ownerId = null)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", "saldos.xlsx");
        var query = $"warehouseId={warehouseId}" + (ownerId is null ? string.Empty : $"&ownerId={ownerId}");
        return client.PostAsync($"{WmsUrl()}/imports/initial-balances/preview?{query}", content, Ct);
    }

    private static async Task<InitialBalancePreviewDto> PreviewAsync(HttpClient client, Guid warehouseId, byte[] file)
    {
        var response = await PreviewRawAsync(client, warehouseId, file);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return (await response.Content.ReadFromJsonAsync<InitialBalancePreviewDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> CommitRawAsync(HttpClient client, Guid previewId, long version, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/imports/initial-balances/commit")
        {
            Content = JsonContent.Create(new { previewId, version }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<long> WarehouseVersionAsync(Testcontainers.PostgreSql.PostgreSqlContainer database, Guid warehouseId) =>
        await ScalarAsync<long>(database, "SELECT version FROM wms.warehouses WHERE id = @id", ("id", warehouseId));

    [Fact]
    public async Task ThePreviewResolvesCodesLocationsAndLotsAndStoresThePreview()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var binB = await CreateBinAsync(s.Manager, s.WarehouseId, "A", "A-01-03");
        var lotted = await CreateVariantAsync(factory, "V2", null, [1]);
        await SetTrackingAsync(s.Manager, lotted, "Lot");

        var preview = await PreviewAsync(
            s.Manager,
            s.WarehouseId,
            Workbook([new SheetRow("V1", "A-01-02", 5m), new SheetRow("V2", "a-01-03", 7m, Lot: "L-1")]));

        Assert.True(preview.CanCommit);
        Assert.Equal(2, preview.Rows.Count);
        Assert.All(preview.Rows, row => Assert.Empty(row.Issues));
        Assert.Equal(s.VariantId, preview.Rows[0].VariantId);
        Assert.Equal(s.BinA, preview.Rows[0].LocationId);
        Assert.Equal(lotted, preview.Rows[1].VariantId);
        Assert.Equal(binB.Id, preview.Rows[1].LocationId);
        Assert.Equal("L-1", preview.Rows[1].LotCode);
        Assert.Equal(await WarehouseVersionAsync(database, s.WarehouseId), preview.WarehouseVersion);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.import_previews WHERE id = @id", ("id", preview.PreviewId)));
    }

    [Fact]
    public async Task AnAmbiguousCodeAndAnUnknownLocationBlockTheCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await CreateVariantAsync(factory, "abc", null, [1]);
        await CreateVariantAsync(factory, "ABC", null, [1]);

        var preview = await PreviewAsync(
            s.Manager, s.WarehouseId, Workbook([new SheetRow("Abc", "A-01-02"), new SheetRow("V1", "ZZ-99")]));

        Assert.False(preview.CanCommit);
        Assert.Equal([WmsErrorCodesLiteral.Ambiguous], preview.Rows[0].Issues);
        Assert.Equal([WmsErrorCodesLiteral.UnknownLocation], preview.Rows[1].Issues);

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.preview_not_committable", await CodeOfAsync(commit));
    }

    [Fact]
    public async Task APreviewWithIssuesIs200AndTheCommitIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await PreviewRawAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("NOPE", "A-01-02")]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<InitialBalancePreviewDto>(Ct))!;
        Assert.False(preview.CanCommit);
        Assert.Equal([WmsErrorCodesLiteral.UnknownCode], preview.Rows[0].Issues);
        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.preview_not_committable", await CodeOfAsync(commit));
    }

    [Fact]
    public async Task ARepeatedKeyIsAnIssueAndADifferentLocationIsNot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await CreateBinAsync(s.Manager, s.WarehouseId, "A", "A-01-03");

        var preview = await PreviewAsync(
            s.Manager,
            s.WarehouseId,
            Workbook([new SheetRow("V1", "A-01-02", 1m), new SheetRow("V1", "A-01-02", 2m), new SheetRow("V1", "A-01-03", 3m)]));

        Assert.False(preview.CanCommit);
        Assert.Empty(preview.Rows[0].Issues);
        Assert.Equal([WmsErrorCodesLiteral.Duplicate], preview.Rows[1].Issues);
        Assert.Empty(preview.Rows[2].Issues);
    }

    [Fact]
    public async Task AVirtualLocationInARowIsAnUnknownLocation()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "ADJUSTMENT")]));

        Assert.False(preview.CanCommit);
        Assert.Equal([WmsErrorCodesLiteral.UnknownLocation], preview.Rows[0].Issues);
    }

    [Fact]
    public async Task TheCommitPostsOneInitialLoadBatchAndIsIdempotent()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var binB = await CreateBinAsync(s.Manager, s.WarehouseId, "A", "A-01-03");
        var preview = await PreviewAsync(
            s.Manager,
            s.WarehouseId,
            Workbook([new SheetRow("V1", "A-01-02", 5m), new SheetRow("V1", "A-01-03", 3m, Uom: "EMPAQUE x12")]));
        Assert.True(preview.CanCommit);

        var first = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-3");

        Assert.True(first.StatusCode == HttpStatusCode.Created, await BodyAsync(first));
        var commit = (await first.Content.ReadFromJsonAsync<InitialBalanceCommitDto>(Ct))!;
        Assert.Equal(2, commit.MovementCount);
        Assert.Equal("InitialLoad", await ScalarAsync<string>(database, "SELECT reason FROM wms.adjustments WHERE id = @id", ("id", commit.AdjustmentId)));
        Assert.Equal("Posted", await ScalarAsync<string>(database, "SELECT status FROM wms.adjustments WHERE id = @id", ("id", commit.AdjustmentId)));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                database,
                "SELECT count(*) FROM wms.stock_movements WHERE reason = 'InitialLoad' AND batch_id = @batch",
                ("batch", commit.BatchId)));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        // 3 EMPAQUE x12 son 36 en base, y el movimiento guarda cómo se capturó.
        Assert.Equal(36m, await OnHandAsync(database, binB.Id, s.VariantId, StockStatus.Available));
        Assert.Equal(
            12m,
            await ScalarAsync<decimal>(
                database,
                "SELECT conversion_factor FROM wms.stock_movements WHERE batch_id = @batch AND captured_uom IS NOT NULL",
                ("batch", commit.BatchId)));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.stock.imported", preview.PreviewId.ToString()));

        var again = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-3");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(commit.BatchId, (await again.Content.ReadFromJsonAsync<InitialBalanceCommitDto>(Ct))!.BatchId);
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE reason = 'InitialLoad'"));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        // Otra clave sobre el mismo preview ya consumido no carga el saldo dos veces.
        var other = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-3-OTHER");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AStaleWarehouseVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion + 1, "IMP-4");

        Assert.Equal(HttpStatusCode.PreconditionFailed, commit.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE reason = 'InitialLoad'"));
    }

    [Fact]
    public async Task AnExpiredPreviewIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));
        await ExecuteAsync(database, "UPDATE wms.import_previews SET expires_at = now() - interval '1 minute'");

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-5");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.preview_expired", await CodeOfAsync(commit));
    }

    [Fact]
    public async Task AFileWithMoreThan500RowsIs422TooManyRows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var lotted = await CreateVariantAsync(factory, "V2", null, [1]);
        await SetTrackingAsync(s.Manager, lotted, "Lot");

        var tooMany = await PreviewRawAsync(s.Manager, s.WarehouseId, Workbook(LotRows(501)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal("wms.import.too_many_rows", await CodeOfAsync(tooMany));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.import_previews"));

        var exactly = await PreviewAsync(s.Manager, s.WarehouseId, Workbook(LotRows(500)));

        Assert.True(exactly.CanCommit);
        Assert.Equal(500, exactly.Rows.Count);
    }

    [Fact]
    public async Task AFiveHundredRowCommitPostsOneBatchWithOneMovementPerRow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var lotted = await CreateVariantAsync(factory, "V2", null, [1]);
        await SetTrackingAsync(s.Manager, lotted, "Lot");
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook(LotRows(500)));
        Assert.True(preview.CanCommit);

        var response = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "IMP-500");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var commit = (await response.Content.ReadFromJsonAsync<InitialBalanceCommitDto>(Ct))!;
        Assert.Equal(500, commit.MovementCount);
        Assert.Equal(
            500L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE batch_id = @batch", ("batch", commit.BatchId)));
        Assert.Equal(500L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.lots WHERE variant_id = @v", ("v", lotted)));
    }

    [Fact]
    public async Task TheCommitWithoutTheHeaderIs428()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, commit.StatusCode);
        Assert.Equal("wms.idempotency_key_required", await CodeOfAsync(commit));
    }

    [Fact]
    public async Task AFileWithoutTheExpectedColumnsIs422InvalidFile()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await PreviewRawAsync(
            s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")], headers: ["Code", "Location", "Qty"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ThePreviewNeedsTheProposePermission()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        using var reader = CreateReader(factory);

        var response = await PreviewRawAsync(reader, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static IEnumerable<SheetRow> LotRows(int count) =>
        Enumerable.Range(1, count).Select(index => new SheetRow("V2", "A-01-02", 1m, Lot: $"L-{index:D4}"));

    private static class WmsErrorCodesLiteral
    {
        public const string Ambiguous = "wms.import.ambiguous_code";
        public const string UnknownCode = "wms.import.unknown_code";
        public const string UnknownLocation = "wms.import.unknown_location";
        public const string Duplicate = "wms.import.duplicate_row";
    }
}
