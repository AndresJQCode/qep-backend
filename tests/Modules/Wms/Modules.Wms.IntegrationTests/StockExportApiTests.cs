using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>La exportación de saldos (RF-030): arma el Excel en el request, lo deja en el almacenamiento y encola el correo por el
/// outbox con su propio evento, <c>wms.export-ready.v1</c>. El almacenamiento se sustituye por un doble que captura los bytes.</summary>
public sealed class StockExportApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeExportStorage : IWmsExportStorage
    {
        public byte[]? Content { get; private set; }

        public string? FileName { get; private set; }

        public Guid? FileId { get; private set; }

        public Task<WmsExportUpload> UploadAsync(Guid tenantId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken)
        {
            FileId = fileId;
            FileName = fileName;
            Content = content;
            return Task.FromResult(new WmsExportUpload($"https://r2.test/exports/{fileId:N}.xlsx?X-Amz-Signature=abc", DateTimeOffset.UtcNow.AddHours(24)));
        }
    }

    private static async Task<(Testcontainers.PostgreSql.PostgreSqlContainer Database, QepApiFactory Factory, FakeExportStorage Storage)> OpenWithStorageAsync()
    {
        var storage = new FakeExportStorage();
        var (database, factory) = await OpenAsync(configureServices: services => services.AddScoped<IWmsExportStorage>(_ => storage));
        return (database, factory, storage);
    }

    // §24.3: los filtros viajan en el cuerpo. Sin cuerpo se exporta sin filtros.
    private static Task<HttpResponseMessage> ExportAsync(HttpClient client, object? filters = null) =>
        filters is null
            ? client.PostAsync($"{WmsUrl()}/stock/export", content: null, Ct)
            : client.PostAsJsonAsync($"{WmsUrl()}/stock/export", filters, Ct);

    [Fact]
    public async Task ExportingAcceptsUploadsAndQueuesTheEmail()
    {
        var (database, factory, storage) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10m);

        var response = await ExportAsync(s.Manager);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await BodyAsync(response));
        var accepted = (await response.Content.ReadFromJsonAsync<StockExportAcceptedDto>(Ct))!;

        // Una sola fila: el saldo negativo de SUPPLIER es de una virtual y no se expone como existencia.
        Assert.Equal(1, accepted.RowCount);
        Assert.Equal(storage.FileId, accepted.FileId);
        Assert.StartsWith("saldos-", accepted.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".xlsx", accepted.FileName, StringComparison.Ordinal);
        Assert.Equal(accepted.FileName, storage.FileName);

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                database,
                """
                SELECT count(*) FROM platform.outbox_messages
                WHERE event_name = 'wms.export-ready.v1' AND payload ->> 'fileId' = @id AND payload ->> 'downloadUrl' LIKE 'https://r2.test/%'
                """,
                ("id", accepted.FileId.ToString())));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'suppliers.export-ready.v1'"));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.stock.exported", accepted.FileId.ToString()));

        using var stream = new MemoryStream(storage.Content!);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        Assert.Equal("Código", sheet.Cell(1, 5).GetString());
        Assert.Equal("Para picking", sheet.Cell(1, 11).GetString());
        Assert.Equal("V1", sheet.Cell(2, 5).GetString());
        Assert.Equal(10d, sheet.Cell(2, 10).GetDouble());
        Assert.Equal(10d, sheet.Cell(2, 11).GetDouble());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    [Fact]
    public async Task ExportingWithoutRowsIs422Empty()
    {
        var (database, factory, storage) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await ExportAsync(s.Manager);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.export.empty", await CodeOfAsync(response));
        Assert.Null(storage.Content);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'wms.export-ready.v1'"));
    }

    [Fact]
    public async Task ExportingNeedsTheExportPermission()
    {
        var (database, factory, storage) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10m);
        using var reader = CreateReader(factory);

        var response = await ExportAsync(reader);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(storage.Content);
    }

    [Fact]
    public async Task ExportingAppliesTheStockFilters()
    {
        var (database, factory, storage) = await OpenWithStorageAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var second = await CreateVariantAsync(factory, "V2", null, [1]);
        await StockAsync(factory, s, s.BinA, 10m);
        await StockAsync(factory, s, s.BinA, 4m, variant: second);

        var everything = await ExportAsync(s.Manager);
        var other = await ExportAsync(s.Manager, new { variantId = Guid.CreateVersion7() });
        var matching = await ExportAsync(s.Manager, new { variantId = s.VariantId, warehouseId = s.WarehouseId });

        // I-4: sin filtro salen las dos variantes; el filtro del cuerpo deja una sola. Un filtro que se ignorara exportaría todo.
        Assert.Equal(HttpStatusCode.Accepted, everything.StatusCode);
        Assert.Equal(2, (await everything.Content.ReadFromJsonAsync<StockExportAcceptedDto>(Ct))!.RowCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, matching.StatusCode);
        Assert.Equal(1, (await matching.Content.ReadFromJsonAsync<StockExportAcceptedDto>(Ct))!.RowCount);
        using var stream = new MemoryStream(storage.Content!);
        using var workbook = new XLWorkbook(stream);
        Assert.Equal("V1", workbook.Worksheets.First().Cell(2, 5).GetString());
        Assert.True(workbook.Worksheets.First().Cell(3, 1).IsEmpty());
    }
}
