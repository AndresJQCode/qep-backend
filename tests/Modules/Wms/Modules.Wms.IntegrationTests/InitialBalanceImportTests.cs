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

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "2eae1c2c-1da5-5f89-9feb-4faad316be1e");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(commit));
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
        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "4638d991-5a23-5591-96ab-74c1a94affcc");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(commit));
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

        var first = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "99ffeff6-0b32-5152-a527-3cc4d2f3ece4");

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

        var again = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "99ffeff6-0b32-5152-a527-3cc4d2f3ece4");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(commit.BatchId, (await again.Content.ReadFromJsonAsync<InitialBalanceCommitDto>(Ct))!.BatchId);
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE reason = 'InitialLoad'"));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        // Otra clave sobre el mismo preview ya consumido no carga el saldo dos veces.
        var other = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "4c1c12c2-866d-5167-ba54-855591f61443");
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

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion + 1, "f690a1d7-5bef-5e95-885e-5b1b383b7898");

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

        var commit = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "59e62394-9e30-5e94-997f-4574d3ce0264");

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

        var response = await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "924f0b54-01a4-5671-a656-2232807ffd44");

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

    // I-1: el spec exige wms.adjustment.propose para importar (§24.2 y §24.3); approve solo no alcanza.
    [Fact]
    public async Task AProposeOnlyRoleCanPreviewAndCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        using var proposer = CreateClient(factory, SubjectId, TenantId, WmsPermissions.AdjustmentPropose);

        var preview = await PreviewAsync(proposer, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));
        var commit = await CommitRawAsync(proposer, preview.PreviewId, preview.WarehouseVersion, "3ea4f27e-c876-5e2d-a6cb-7dac0b4181f1");

        Assert.True(commit.StatusCode == HttpStatusCode.Created, await BodyAsync(commit));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AnApproveOnlyRoleCannotCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));
        using var approver = CreateClient(factory, SubjectId, TenantId, WmsPermissions.AdjustmentApprove);

        var commit = await CommitRawAsync(approver, preview.PreviewId, preview.WarehouseVersion, "8ec200fa-59e1-5a63-b2a6-6e37ab1fd0a7");

        Assert.Equal(HttpStatusCode.Forbidden, commit.StatusCode);
    }

    // I-5: ni un zip que declara demasiado ni uno que miente sobre su tamaño llegan a ClosedXML.
    [Fact]
    public async Task AZipBombIs422InvalidFileBeforeClosedXmlSeesIt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await PreviewRawAsync(s.Manager, s.WarehouseId, ZipBytes(entryBytes: 30 * 1024 * 1024));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.import_previews"));
    }

    private static byte[] ZipBytes(int entryBytes)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", System.IO.Compression.CompressionLevel.SmallestSize);
            using var writer = entry.Open();
            writer.Write(new byte[entryBytes]);
        }

        return stream.ToArray();
    }

    // I-6: la fecha de vencimiento de un lote la fija la primera fila que lo crea; otra distinta en la hoja, o distinta de la de un lote
    // que ya existe, se perdería en silencio.
    [Fact]
    public async Task ALotWithTwoExpiryDatesInTheFileOrDifferentFromTheExistingOneIsAnIssue()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await CreateBinAsync(s.Manager, s.WarehouseId, "A", "A-01-03");
        var lotted = await CreateVariantAsync(factory, "V2", null, [1]);
        await SetTrackingAsync(s.Manager, lotted, "Lot", tracksExpiry: true);
        var january = new DateOnly(2027, 1, 31);

        var twoDates = await PreviewAsync(
            s.Manager,
            s.WarehouseId,
            Workbook([
                new SheetRow("V2", "A-01-02", 1m, Lot: "L-1", Expiration: january),
                new SheetRow("V2", "A-01-03", 1m, Lot: "L-1", Expiration: new DateOnly(2027, 6, 30)),
            ]));

        Assert.False(twoDates.CanCommit);
        Assert.Empty(twoDates.Rows[0].Issues);
        Assert.Equal(["wms.import.lot_expiry_conflict"], twoDates.Rows[1].Issues);

        var first = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V2", "A-01-02", 1m, Lot: "L-1", Expiration: january)]));
        (await CommitRawAsync(s.Manager, first.PreviewId, first.WarehouseVersion, "33f97aba-6ab5-5853-9a8f-82cf8c32666f")).EnsureSuccessStatusCode();

        var different = await PreviewAsync(
            s.Manager, s.WarehouseId, Workbook([new SheetRow("V2", "A-01-03", 1m, Lot: "L-1", Expiration: new DateOnly(2028, 1, 31))]));
        var same = await PreviewAsync(
            s.Manager, s.WarehouseId, Workbook([new SheetRow("V2", "A-01-03", 1m, Lot: "L-1", Expiration: january)]));

        Assert.Equal(["wms.import.lot_expiry_conflict"], different.Rows[0].Issues);
        Assert.Empty(same.Rows[0].Issues);
        Assert.True(same.CanCommit);
    }

    // I-7 (pendiente de confirmación del owner): subir dos veces el mismo archivo duplicaría el saldo, así que la segunda carga se bloquea.
    [Fact]
    public async Task UploadingTheSameFileTwiceBlocksTheSecondCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var file = Workbook([new SheetRow("V1", "A-01-02", 5m)]);
        var first = await PreviewAsync(s.Manager, s.WarehouseId, file);
        (await CommitRawAsync(s.Manager, first.PreviewId, first.WarehouseVersion, "e6cb8a14-3639-5848-b33c-012ce6a07bab")).EnsureSuccessStatusCode();

        var second = await PreviewAsync(s.Manager, s.WarehouseId, file);

        Assert.False(second.CanCommit);
        Assert.Equal(["wms.import.stock_exists"], second.Rows[0].Issues);
        var commit = await CommitRawAsync(s.Manager, second.PreviewId, second.WarehouseVersion, "e4103539-1738-5ed0-a1c5-1f9290b9fa4f");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(commit));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    // I-7, segunda mitad: dos previews hechos antes del primer commit pasan los dos el chequeo del preview. El commit vuelve a revisar el
    // saldo dentro de su transacción, y el segundo no duplica; su preview queda disponible, como en cualquier commit que falla.
    [Fact]
    public async Task TwoPreviewsOfTheSameFileCannotBothCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var file = Workbook([new SheetRow("V1", "A-01-02", 5m)]);
        var first = await PreviewAsync(s.Manager, s.WarehouseId, file);
        var second = await PreviewAsync(s.Manager, s.WarehouseId, file);
        Assert.True(first.CanCommit);
        Assert.True(second.CanCommit);

        var one = await CommitRawAsync(s.Manager, first.PreviewId, first.WarehouseVersion, "2eae1c2c-1da5-5f89-9feb-4faad316be1e");
        var two = await CommitRawAsync(s.Manager, second.PreviewId, second.WarehouseVersion, "4638d991-5a23-5591-96ab-74c1a94affcc");

        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, two.StatusCode);
        Assert.Equal("wms.import.invalid_file", await CodeOfAsync(two));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.import_previews WHERE id = @id", ("id", second.PreviewId)));
    }

    // Los dos commits a la vez: el segundo espera el lock de la importación hasta que el primero confirma, y entonces ve el saldo.
    [Fact]
    public async Task TwoSimultaneousCommitsOfTheSameFileLoadTheBalanceOnce()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var file = Workbook([new SheetRow("V1", "A-01-02", 5m)]);
        var first = await PreviewAsync(s.Manager, s.WarehouseId, file);
        var second = await PreviewAsync(s.Manager, s.WarehouseId, file);

        var results = await Task.WhenAll(
            CommitRawAsync(s.Manager, first.PreviewId, first.WarehouseVersion, "9e191c33-0eb9-5d95-b72b-2bd4dc1be6ab"),
            CommitRawAsync(s.Manager, second.PreviewId, second.WarehouseVersion, "8c90c422-503c-5b86-b277-30c253a01154"));

        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    // M-6: la auditoría dice quién preparó la carga además de quién la confirmó.
    [Fact]
    public async Task TheAuditEntryRecordsWhoPreparedThePreview()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var preview = await PreviewAsync(s.Manager, s.WarehouseId, Workbook([new SheetRow("V1", "A-01-02")]));
        (await CommitRawAsync(s.Manager, preview.PreviewId, preview.WarehouseVersion, "4f7832c6-8c03-5e44-b02c-afb87a0bb29e")).EnsureSuccessStatusCode();

        var prepared = await ScalarAsync<string>(
            database,
            "SELECT changed_fields ->> 'preparedBy' FROM audit.entries WHERE action = 'wms.stock.imported' AND resource_id = @id",
            ("id", preview.PreviewId.ToString()));

        Assert.Equal(SubjectId, prepared);
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
