using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de cotizaciones: lee por lotes con el mismo filtro que el listado, escribe las
/// columnas de la tabla en su orden y sube el archivo con el id del job. Y clasifica sus fallos
/// (D11): lo que no se arregla reintentando es definitivo.
/// </summary>
public sealed class QuotationsExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El rango guardado en el job, cortado en el día de Bogotá al procesar (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WritesTheListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Cotizaciones", writer.SheetName);
        Assert.Equal(
            ["Numero", "Fecha", "Cliente", "Asesor", "Estado", "Moneda", "Total"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("QUO-2026-0001", row[0].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[1].Text);
        Assert.Equal("Ferretería El Tornillo", row[2].Text);
        // El nombre, igual que la tabla (spec 2026-09-11, D1, nota del 2026-09-14).
        Assert.Equal("Asesora Uno", row[3].Text);
        // La etiqueta de la tabla, no el nombre del enum (spec 2026-09-13, A7).
        Assert.Equal("Borrador", row[4].Text);
        Assert.Equal("COP", row[5].Text);
        Assert.Equal(0m, row[6].Number);
        Assert.Null(row[6].Text);
        Assert.True(writer.Disposed);
    }

    // Sin nombre en la membresía (owner, sembrados) el archivo cae al correo, igual que la tabla.
    [Fact]
    public async Task WritesTheAdvisorEmailWhenTheMemberHasNoName()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")),
            writer,
            advisors: new StubQuotationAdvisorLookup("asesora@qcode.co"));

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(writer.Rows)[3].Text);
    }

    // D8: la memoria queda acotada al lote. Mil y una filas son dos consultas.
    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var quotations = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewQuotation($"QUO-2026-{number:0000}"))
            .ToArray();
        var repository = new StubQuotationListRepository(quotations);
        var writer = new RecordingExportWorkbookWriter();

        var result = await NewProcessor(repository, writer).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
        Assert.Equal(ExportJobLimits.BatchSize + 1, writer.Rows.Count);
    }

    // Keyset (D8): cada lote pide lo que viene después de la última fila del anterior, nunca un
    // offset. Todas del mismo instante: el número desempata, de mayor a menor.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var quotations = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewQuotation($"QUO-2026-{number:0000}"))
            .ToArray();
        var repository = new StubQuotationListRepository(quotations);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new QuotationExportCursor?[] { null, new QuotationExportCursor(Now, "QUO-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsUnderTheJobAndReturnsWhatTheEmailNeeds()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), storage: storage);

        var result = await processor.ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedUpload(TenantId, job.Id, "cotizaciones-2026-09-12-1530.xlsx", RecordingExportWorkbookWriter.CompletedPath),
            storage.Upload);
        Assert.Equal(
            new ExportJobResult(
                "cotizaciones-2026-09-12-1530.xlsx",
                1,
                $"https://r2.test/exports/tenants/{TenantId:N}/jobs/{job.Id:N}.xlsx",
                StubExportJobProcessor.LinkExpiresAt),
            result);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var job = NewJob(new QuotationsExportFilters(ClientId, AdvisorId.Value, "sent", From, To, null, "0001"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, FromUtc, BeforeUtc, "0001"),
            repository.LastExportSearch);
    }

    // Había filas al pedir y ya no al procesar: reintentar da lo mismo.
    [Fact]
    public async Task NoRowsWhenItRunsIsDefinitiveAndUploadsNothing()
    {
        var writer = new RecordingExportWorkbookWriter();
        var storage = new RecordingExportFileStorage();
        var processor = NewProcessor(new StubQuotationListRepository(), writer, storage);

        var error = await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken));

        Assert.StartsWith("Empty:", error.Message, StringComparison.Ordinal);
        Assert.Null(storage.Upload);
        // El temporal se borra también en este camino, que sale antes de Complete().
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task UnreadableFiltersAreDefinitive()
    {
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), ExportJobKind.Quotations, "not json", Now);

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new QuotationsExportFilters(null, null, "Approved", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubQuotationListRepository(NewQuotation("QUO-2026-0001")))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    // R2 caído no es definitivo: la excepción sube tal cual y el runner reintenta. El temporal se
    // borra igual.
    [Fact]
    public async Task AStorageFailureIsTransientAndStillDisposesTheWorkbook()
    {
        var writer = new RecordingExportWorkbookWriter();
        var storage = new RecordingExportFileStorage { Failure = new IOException("r2 unavailable") };
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), writer, storage);

        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken));

        Assert.True(writer.Disposed);
    }

    private static ExportJob NewJob(QuotationsExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Quotations,
            ExportJobFilters.Serialize(filters ?? new QuotationsExportFilters(null, null, null, From, To, null, null)),
            Now);

    private static Quotation NewQuotation(string number) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            number,
            ClientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static QuotationsExportProcessor NewProcessor(
        StubQuotationListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        StubQuotationAdvisorLookup? advisors = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            new FixedTenantClock(Now));
}
