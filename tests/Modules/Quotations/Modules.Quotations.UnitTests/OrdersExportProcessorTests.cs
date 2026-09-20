using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El Excel de pedidos con las columnas del ERP contable (ajuste 2026-09-20): una fila por línea
/// de producto, con los datos del pedido repetidos en cada una. Reemplaza a la tabla de pedidos
/// (Pedido, Cliente, Asesor...) que este archivo tenía hasta el spec 2026-09-15/17 — ese formato
/// sigue siendo lo que muestra la pantalla (order-table.tsx), pero ya no es lo que exporta este
/// procesador.
/// </summary>
public sealed class OrdersExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid OtherProductId = Guid.CreateVersion7();
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly Guid CityId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El rango guardado en el job, cortado en el día de Bogotá al procesar (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef DefaultCustomer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
        Email: "cliente@ejemplo.co", CityName: "Bogotá");

    private static readonly QuotationProductRef DefaultProduct = new(
        ProductId, "Tornillo hexagonal", "TOR-001", ImageUrl: null, Scales: []);

    [Fact]
    public async Task WritesTheErpColumnsInOrder()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Pedidos", writer.SheetName);
        Assert.Equal(
            [
                "EMPRESA", "Forma de pago 1", "V. Consignacion 1", "Cod. Producto", "U.Medida",
                "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
                "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
                "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
            ],
            writer.Columns.Select(column => column.Header));
    }

    [Fact]
    public async Task AnOrderWithTwoLinesWritesTwoRowsSharingTheOrderLevelData()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            paymentMethod: "Transferencia",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        // Los campos del pedido se repiten igual en las dos filas.
        foreach (var cells in writer.Rows)
        {
            Assert.Equal("Transferencia", cells[1].Text);
            Assert.Equal(row.Quotation.Total, cells[2].Number);
            Assert.Equal("PED-2026-0001", cells[17].Text);
        }

        // Cada fila trae su propia línea.
        Assert.Equal(2m, writer.Rows[0][5].Number);
        Assert.Equal(5m, writer.Rows[1][5].Number);
    }

    [Fact]
    public async Task AnOrderWithoutItemsContributesNoRows()
    {
        var writer = new RecordingExportWorkbookWriter();
        var withItems = NewRow("PED-2026-0001");
        var withoutItems = NewRow("PED-2026-0002", items: []);

        await NewProcessor(new StubOrderListRepository(withItems, withoutItems), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("PED-2026-0001", row[17].Text);
    }

    [Fact]
    public async Task EmpresaComesFromTheBillingAccountsCompany()
    {
        var writer = new RecordingExportWorkbookWriter();
        var billingAccount = new QuotationBillingAccount
        {
            CompanyId = CompanyId,
            BankName = "Bancolombia",
            AccountNumber = "123",
            Currency = "COP",
        };
        var companies = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>
        {
            [CompanyId] = new(CompanyId, "Raíces Orgánicas", "901.846.471-5", IsActive: true, Address: null, Phone: null, BankAccounts: []),
        });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: billingAccount)),
                writer,
                companies: companies)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Raíces Orgánicas", Assert.Single(writer.Rows)[0].Text);
    }

    [Fact]
    public async Task EmpresaIsEmptyWithoutABillingAccount()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: null)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[0].Text);
    }

    // U.Medida (ajuste 2026-09-20): la restricción de la escala del producto que cubre la
    // cantidad de la línea, recalculada contra el catálogo de hoy — la línea no guardó a cuál
    // respondió al cotizarse.
    [Fact]
    public async Task UMedidaShowsTheMultipleWhenTheMatchingScaleIsAMultiple()
    {
        var writer = new RecordingExportWorkbookWriter();
        var product = DefaultProduct with
        {
            Scales = [new QuotationPriceScaleRef(1, 100, 0m, QuotationPriceScaleRestriction.Multiple, 3, null)],
        };
        var products = new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef> { [ProductId] = product });

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, products: products)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Múltiplo de 3", Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task UMedidaShowsThePackagingUnitWhenTheMatchingScaleIsAPackage()
    {
        var writer = new RecordingExportWorkbookWriter();
        var product = DefaultProduct with
        {
            Scales = [new QuotationPriceScaleRef(1, 100, 0m, QuotationPriceScaleRestriction.PackagingUnit, null, 12)],
        };
        var products = new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef> { [ProductId] = product });

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, products: products)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Empaque de 12", Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task UMedidaIsEmptyWhenNoScaleCoversTheQuantity()
    {
        var writer = new RecordingExportWorkbookWriter();
        var product = DefaultProduct with
        {
            Scales = [new QuotationPriceScaleRef(50, 100, 0m, QuotationPriceScaleRestriction.Multiple, 3, null)],
        };
        var products = new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef> { [ProductId] = product });

        // La línea por defecto pide cantidad 2, fuera del rango 50-100 de la única escala.
        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, products: products)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[4].Text);
    }

    [Fact]
    public async Task NotaDetalleIsAlwaysEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[9].Text);
    }

    [Fact]
    public async Task ObservacionesIsTheQuotationsNotes()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", notes: "Frágil")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Frágil", Assert.Single(writer.Rows)[19].Text);
    }

    [Fact]
    public async Task DocumentoIsTheCustomersCuc()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("CUC-001", Assert.Single(writer.Rows)[16].Text);
    }

    // Sin parte de entrega propia: "los mismos datos del cliente" (el caso normal) resuelve
    // contra la ficha del cliente.
    [Fact]
    public async Task FallsBackToTheCustomersDataWithoutAShippingParty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: QuotationParties.Empty)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("Bogotá", row[15].Text);
        Assert.Equal("Calle 1 # 2-3", row[18].Text);
        Assert.Equal("3001234567", row[20].Text);
        Assert.Equal("cliente@ejemplo.co", row[21].Text);
    }

    // Con parte de entrega propia, ésta gana sobre el cliente, incluida su ciudad (que se
    // resuelve aparte: la parte sólo guarda el id).
    [Fact]
    public async Task AShippingPartyWinsOverTheCustomersData()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails
            {
                Name = "Bodega Norte",
                Phone = "6015550000",
                Email = "bodega@ejemplo.co",
                Address = "Zona Franca, Bodega 14",
                CityId = CityId,
            });
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string> { [CityId] = "Rionegro" });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)),
                writer,
                geography: geography)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("Rionegro", row[15].Text);
        Assert.Equal("Zona Franca, Bodega 14", row[18].Text);
        Assert.Equal("6015550000", row[20].Text);
        Assert.Equal("bodega@ejemplo.co", row[21].Text);
    }

    [Fact]
    public async Task PaymentDatesFillFromTheProofsUploadedAtInTheTenantsLocalTime()
    {
        var writer = new RecordingExportWorkbookWriter();
        var convertedAt = new DateTimeOffset(2026, 9, 12, 15, 0, 0, TimeSpan.Zero); // 10:00 en Bogotá.

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", publicKeys: ["a.pdf"], at: convertedAt)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("2026-09-12 10:00", row[10].Text);
        Assert.Equal(string.Empty, row[11].Text);
    }

    [Fact]
    public async Task AnOrderWithoutProofsLeavesEveryPaymentDateCellEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(
            [string.Empty, string.Empty, string.Empty, string.Empty, string.Empty],
            Enumerable.Range(10, 5).Select(index => row[index].Text));
    }

    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        var result = await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        // Un producto por pedido en esta siembra: filas de archivo y pedidos leídos coinciden.
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
    }

    // E6: los comprobantes se piden una vez por lote, con los pedidos de ese lote.
    [Fact]
    public async Task ReadsThePaymentProofsOncePerBatchWithTheOrdersOfThatBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
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

    // Keyset (D8): el lote siguiente arranca después del último pedido del anterior.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
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

        var result = await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), storage: storage)
            .ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("pedidos-2026-09-12-1030.xlsx", result.FileName);
        Assert.Equal(job.Id, storage.Upload!.JobId);
    }

    // Spec 2026-09-17, punto 8a: convertido y exportado el 31 de diciembre a las 23:00 en Bogotá, el
    // nombre del archivo no dice 2027.
    [Fact]
    public async Task WritesTheFileNameInTheTenantsLocalTime()
    {
        var newYearsEveInBogota = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

        var result = await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", at: newYearsEveInBogota)),
                tenantClock: new FixedTenantClock(newYearsEveInBogota))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("pedidos-2026-12-31-2300.xlsx", result.FileName);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001"));
        var job = NewJob(new OrdersExportFilters(ClientId, AdvisorId.Value, "approved", "fullpaymentreceived", From, To, null, "PED"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedOrderExportSearch(
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, FromUtc, BeforeUtc, "PED"),
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
            NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")))
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

    private static readonly (Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage)[]
        DefaultItems = [(ProductId, 2m, 1000m, 0m, 19)];

    private static OrderWithQuotation NewRow(
        string orderNumber,
        string? paymentMethod = null,
        IReadOnlyList<string?>? publicKeys = null,
        DateTimeOffset? at = null,
        QuotationParties? parties = null,
        QuotationBillingAccount? billingAccount = null,
        IReadOnlyList<(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage)>? items = null,
        string? notes = null)
    {
        var occurredAt = at ?? Now;
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes, parties ?? QuotationParties.Empty, billingAccount,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, occurredAt);

        foreach (var item in items ?? DefaultItems)
        {
            quotation.AddItemAfterConversion(
                QuotationItemId.New(), item.ProductId, item.Quantity, item.UnitPrice,
                item.DiscountPercentage, item.TaxPercentage, AdvisorId, occurredAt);
        }

        var proofs = (publicKeys ?? [])
            .Select(publicKey => new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m, publicKey))
            .ToArray();
        var order = Order.Create(
            OrderId.New(), TenantId, orderNumber, quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, proofs, occurredAt);
        return new OrderWithQuotation(order, quotation);
    }

    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        FixedTenantClock? tenantClock = null,
        StubQuotationCustomerLookup? customers = null,
        StubQuotationProductLookup? products = null,
        StubQuotationCompanyLookup? companies = null,
        StubQuotationGeographyLookup? geography = null) =>
        new(repository,
            customers ?? new StubQuotationCustomerLookup(DefaultCustomer),
            products ?? new StubQuotationProductLookup(
                new Dictionary<Guid, QuotationProductRef>
                {
                    [ProductId] = DefaultProduct,
                    [OtherProductId] = DefaultProduct with { Id = OtherProductId, Code = "OTR-002" },
                }),
            companies ?? new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            geography ?? new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));
}
