using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de ventas: las columnas de la tabla de ventas en su orden (hallazgo 2 del plan),
/// lectura por lotes con el filtro del listado, y los mismos fallos definitivos que cotizaciones.
/// </summary>
public sealed class SalesExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task WritesTheSalesListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", paymentMethod: null)), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Ventas", writer.SheetName);
        Assert.Equal(
            ["Venta", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("VEN-2026-0001", row[0].Text);
        Assert.Equal("Ferretería El Tornillo", row[1].Text);
        Assert.Equal("asesora@qcode.co", row[2].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[3].Text);
        // Sin forma de pago, la columna cae al estado del pago, igual que la tabla.
        Assert.Equal("PaymentPending", row[4].Text);
        Assert.Equal("Pending", row[5].Text);
        Assert.Equal(0m, row[7].Number);
    }

    [Fact]
    public async Task PagoShowsThePaymentMethodWhenThereIsOne()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", "Transferencia")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Transferencia", Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"VEN-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubSaleListRepository(rows);

        var result = await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
    }

    // Keyset (D8): el lote siguiente arranca después de la última venta del anterior. Todas del
    // mismo instante: el número desempata, de mayor a menor, como en el listado.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"VEN-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubSaleListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new SaleExportCursor?[] { null, new SaleExportCursor(Now, "VEN-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsAsVentasUnderTheJob()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();

        var result = await NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", null)), storage: storage)
            .ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("ventas-2026-09-12-1530.xlsx", result.FileName);
        Assert.Equal(job.Id, storage.Upload!.JobId);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubSaleListRepository(NewRow("VEN-2026-0001", null));
        var job = NewJob(new SalesExportFilters(ClientId, AdvisorId.Value, "approved", "fullpaymentreceived", From, To, null, "VEN"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedSaleExportSearch(
                ClientId, null, AdvisorId, SaleStatus.Approved, SalePaymentStatus.FullPaymentReceived, From, To, "VEN"),
            repository.LastExportSearch);
    }

    [Fact]
    public async Task NoSalesWhenItRunsIsDefinitive()
    {
        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubSaleListRepository()).ProcessAsync(NewJob(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredPaymentStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new SalesExportFilters(null, null, null, "Refunded", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubSaleListRepository(NewRow("VEN-2026-0001", null)))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    private static ExportJob NewJob(SalesExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Sales,
            ExportJobFilters.Serialize(filters ?? new SalesExportFilters(null, null, null, null, From, To, null, null)),
            Now);

    private static SaleWithQuotation NewRow(string saleNumber, string? paymentMethod)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var sale = Sale.Create(
            SaleId.New(), TenantId, saleNumber, quotation.Id, SalePaymentStatus.PaymentPending,
            notes: null, AdvisorId, [], Now);
        return new SaleWithQuotation(sale, quotation);
    }

    private static SalesExportProcessor NewProcessor(
        StubSaleListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
}
