using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de pedidos: las columnas de la tabla de pedidos en su orden (hallazgo 2 del plan),
/// lectura por lotes con el filtro del listado, y los mismos fallos definitivos que cotizaciones.
/// Desde el spec 2026-09-15, también las cuatro columnas de los comprobantes de pago.
/// </summary>
public sealed class OrdersExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El rango guardado en el job, cortado en el día de Bogotá al procesar (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WritesTheOrdersListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", paymentMethod: null)), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Pedidos", writer.SheetName);
        // Las ocho de la tabla en su orden y, después de Total, las cuatro de los comprobantes
        // (spec 2026-09-15, E1).
        Assert.Equal(
            ["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total",
                "Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("PED-2026-0001", row[0].Text);
        Assert.Equal("Ferretería El Tornillo", row[1].Text);
        // El nombre, igual que la tabla (spec 2026-09-11, D1, nota del 2026-09-15).
        Assert.Equal("Asesora Uno", row[2].Text);
        // Now es 15:30 UTC: 10:30 en Bogotá, sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal("2026-09-12 10:30", row[3].Text);
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

    // E2: sin comprobantes, la cantidad es cero y las tres celdas quedan vacías.
    [Fact]
    public async Task AnOrderWithoutProofsCountsZeroAndLeavesTheProofCellsEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(0m, row[8].Number);
        Assert.Equal([string.Empty, string.Empty, string.Empty], row.Skip(9).Select(cell => cell.Text));
        Assert.All(row.Skip(9), cell => Assert.Null(cell.Url));
    }

    // E2 y E3: un comprobante con copia pública es el enlace «Ver» a su URL pública.
    [Fact]
    public async Task AProofWithAPublicCopyIsAVerLinkToItsPublicUrl()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, ["payment-proofs/a.pdf"])), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(1m, row[8].Number);
        Assert.Equal(UrlOf("payment-proofs/a.pdf"), row[9].Url);
        Assert.Equal("Ver", row[9].Text);
        Assert.Equal(string.Empty, row[10].Text);
        Assert.Equal(string.Empty, row[11].Text);
    }

    // E1: tres comprobantes llenan las tres columnas, en el orden en que llegan del repositorio.
    [Fact]
    public async Task ThreeProofsFillTheThreeColumnsInOrder()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow(
                    "PED-2026-0001", null, ["payment-proofs/a.pdf", "payment-proofs/b.pdf", "payment-proofs/c.pdf"])),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(3m, row[8].Number);
        Assert.Equal(
            [UrlOf("payment-proofs/a.pdf"), UrlOf("payment-proofs/b.pdf"), UrlOf("payment-proofs/c.pdf")],
            row.Skip(9).Select(cell => cell.Url));
        Assert.All(row.Skip(9), cell => Assert.Equal("Ver", cell.Text));
    }

    // E1: el cuarto comprobante no tiene columna, pero la cantidad lo cuenta.
    [Fact]
    public async Task AFourthProofOnlyShowsInTheCount()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow(
                    "PED-2026-0001",
                    null,
                    ["payment-proofs/a.pdf", "payment-proofs/b.pdf", "payment-proofs/c.pdf", "payment-proofs/d.pdf"])),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(12, row.Count);
        Assert.Equal(4m, row[8].Number);
        Assert.Equal(
            [UrlOf("payment-proofs/a.pdf"), UrlOf("payment-proofs/b.pdf"), UrlOf("payment-proofs/c.pdf")],
            row.Skip(9).Select(cell => cell.Url));
    }

    // E2: un comprobante privado —de antes de la opción, o adjuntado con ella apagada (P8)— dice
    // «Sin enlace»: que no haya enlace no es lo mismo que no haya comprobante.
    [Fact]
    public async Task AProofWithoutAPublicCopySaysSinEnlace()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, [null, "payment-proofs/b.pdf"])), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(2m, row[8].Number);
        Assert.Equal("Sin enlace", row[9].Text);
        Assert.Null(row[9].Url);
        Assert.Equal(UrlOf("payment-proofs/b.pdf"), row[10].Url);
        Assert.Equal(string.Empty, row[11].Text);
    }

    // P1 y E7: con la opción apagada no hay URL aunque el comprobante tenga copia, y las cuatro
    // columnas salen igual.
    [Fact]
    public async Task WithTheOptionOffEveryProofSaysSinEnlace()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, ["payment-proofs/a.pdf"])),
                writer,
                publisher: new RecordingPaymentProofPublisher(enabled: false))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(12, writer.Columns.Count);
        var row = Assert.Single(writer.Rows);
        Assert.Equal("Sin enlace", row[9].Text);
        Assert.Null(row[9].Url);
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

    // E6: los comprobantes se piden una vez por lote, con los pedidos de ese lote, igual que los
    // nombres y los correos.
    [Fact]
    public async Task ReadsThePaymentProofsOncePerBatchWithTheOrdersOfThatBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.PaymentProofRequests.Count);
        Assert.Equal(ExportJobLimits.BatchSize, repository.PaymentProofRequests[0].Count);
        // El lote corto trae el de número más bajo: el listado va de mayor a menor.
        Assert.Equal(
            rows.Single(row => row.Order.OrderNumber == "PED-2026-0001").Order.Id,
            Assert.Single(repository.PaymentProofRequests[1]));
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

        Assert.Equal("pedidos-2026-09-12-1030.xlsx", result.FileName);
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
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, FromUtc, BeforeUtc, "PED"),
            repository.LastExportSearch);
    }

    // Spec 2026-09-17, punto 8a: convertido y exportado el 31 de diciembre a las 23:00 en Bogotá, la
    // celda y el nombre del archivo no dicen 2027.
    [Fact]
    public async Task WritesTheDateAndTheFileNameInTheTenantsLocalTime()
    {
        var newYearsEveInBogota = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);
        var writer = new RecordingExportWorkbookWriter();

        var result = await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, at: newYearsEveInBogota)),
                writer,
                tenantClock: new FixedTenantClock(newYearsEveInBogota))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("2026-12-31 23:00", Assert.Single(writer.Rows)[3].Text);
        Assert.Equal("pedidos-2026-12-31-2300.xlsx", result.FileName);
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

    private static string UrlOf(string publicKey) => $"{RecordingPaymentProofPublisher.BaseUrl}/{publicKey}";

    private static ExportJob NewJob(OrdersExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Orders,
            ExportJobFilters.Serialize(filters ?? new OrdersExportFilters(null, null, null, null, From, To, null, null)),
            Now);

    // Un comprobante por clave, en ese orden; null es un comprobante privado. Pago pendiente siempre:
    // el dominio lo admite con comprobantes o sin ellos, y así la columna Pago no cambia.
    private static OrderWithQuotation NewRow(
        string orderNumber,
        string? paymentMethod,
        IReadOnlyList<string?>? publicKeys = null,
        DateTimeOffset? at = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, at ?? Now);
        var proofs = (publicKeys ?? [])
            .Select(publicKey => new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m, publicKey))
            .ToArray();
        var order = Order.Create(
            OrderId.New(), TenantId, orderNumber, quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, proofs, at ?? Now);
        return new OrderWithQuotation(order, quotation);
    }

    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        StubQuotationAdvisorLookup? advisors = null,
        RecordingPaymentProofPublisher? publisher = null,
        FixedTenantClock? tenantClock = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            publisher ?? new RecordingPaymentProofPublisher(),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));
}
