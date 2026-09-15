using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de pedidos: las columnas de la tabla de pedidos en su orden (hallazgo 2 del plan),
/// lectura por lotes con el filtro del listado, y los mismos fallos definitivos que cotizaciones.
/// </summary>
public sealed class OrdersExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task WritesTheOrdersListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", paymentMethod: null)), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Pedidos", writer.SheetName);
        Assert.Equal(
            ["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("PED-2026-0001", row[0].Text);
        Assert.Equal("Ferretería El Tornillo", row[1].Text);
        // El nombre, igual que la tabla (spec 2026-09-11, D1, nota del 2026-09-15).
        Assert.Equal("Asesora Uno", row[2].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[3].Text);
        // Sin forma de pago, la columna cae a la etiqueta del estado del pago, igual que la tabla
        // (spec 2026-09-13, A7).
        Assert.Equal("Pago pendiente", row[4].Text);
        Assert.Equal("Pendiente", row[5].Text);
        Assert.Equal(0m, row[7].Number);
    }

    // Sin nombre en la membresía (owner, sembrados) el archivo cae al correo, igual que la tabla.
    [Fact]
    public async Task WritesTheAdvisorEmailWhenTheMemberHasNoName()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(
            new StubOrderListRepository(NewRow("PED-2026-0001", paymentMethod: null)),
            writer,
            advisors: new StubQuotationAdvisorLookup("asesora@qcode.co"));

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(writer.Rows)[2].Text);
    }

    [Fact]
    public async Task PagoShowsThePaymentMethodWhenThereIsOne()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", "Transferencia")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Transferencia", Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        var result = await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
    }

    // Keyset (D8): el lote siguiente arranca después del último pedido del anterior. Todas del
    // mismo instante: el número desempata, de mayor a menor, como en el listado.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new OrderExportCursor?[] { null, new OrderExportCursor(Now, "PED-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsAsPedidosUnderTheJob()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();

        var result = await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)), storage: storage)
            .ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("pedidos-2026-09-12-1530.xlsx", result.FileName);
        Assert.Equal(job.Id, storage.Upload!.JobId);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", null));
        var job = NewJob(new OrdersExportFilters(ClientId, AdvisorId.Value, "approved", "fullpaymentreceived", From, To, null, "PED"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedOrderExportSearch(
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, From, To, "PED"),
            repository.LastExportSearch);
    }

    [Fact]
    public async Task NoOrdersWhenItRunsIsDefinitive()
    {
        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubOrderListRepository()).ProcessAsync(NewJob(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredPaymentStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new OrdersExportFilters(null, null, null, "Refunded", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    private static ExportJob NewJob(OrdersExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Orders,
            ExportJobFilters.Serialize(filters ?? new OrdersExportFilters(null, null, null, null, From, To, null, null)),
            Now);

    private static OrderWithQuotation NewRow(string orderNumber, string? paymentMethod)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var order = Order.Create(
            OrderId.New(), TenantId, orderNumber, quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, [], Now);
        return new OrderWithQuotation(order, quotation);
    }

    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
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
            new FixedClock(Now));
}
