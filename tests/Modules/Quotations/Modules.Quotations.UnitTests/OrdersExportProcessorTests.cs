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
                "EMPRESA", "Cod. Producto",
                "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
                "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
                "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
                "Cod. Asesor", "Banco", "Cuenta",
                "V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2",
                "V. Comprobante 3", "URL Comprobante 3", "V. Comprobante 4", "URL Comprobante 4",
                "V. Comprobante 5", "URL Comprobante 5",
                "Valor Unit sin IVA",
            ],
            writer.Columns.Select(column => column.Header));
    }

    // 2026-09-24: "Valor Unit sin IVA" va al final, por la misma razón que "Cod. Asesor". Es el
    // precio unitario con el IVA que trae adentro quitado; "Valor Unit" sigue siendo el precio con
    // IVA, que es como se carga QuotationItem.UnitPrice. El descuento no entra en ninguno de los dos.
    [Fact]
    public async Task ValorUnitSinIvaIsTheLastColumnWithTheVatRemovedFromTheUnitPrice()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", items: [(ProductId, 3m, 119_000m, 10m, 19)]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Valor Unit sin IVA", writer.Columns[^1].Header);
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, cells.Count);
        Assert.Equal(119_000m, cells[3].Number);
        Assert.Equal(100_000m, cells[^1].Number);
        Assert.Null(cells[^1].Text);
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
            // Sin forma de pago ni valor a consignar: el pedido la trae y el Excel no.
            Assert.DoesNotContain(cells, cell => cell.Text == "Transferencia");
            Assert.Equal("PED-2026-0001", cells[14].Text);
        }

        // Cada fila trae su propia línea.
        Assert.Equal(2m, writer.Rows[0][2].Number);
        Assert.Equal(5m, writer.Rows[1][2].Number);
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
        Assert.Equal("PED-2026-0001", row[14].Text);
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

    [Fact]
    public async Task NotaDetalleIsAlwaysEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[6].Text);
    }

    [Fact]
    public async Task ObservacionesIsTheQuotationsNotes()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", notes: "Frágil")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Frágil", Assert.Single(writer.Rows)[16].Text);
    }

    [Fact]
    public async Task DocumentoIsTheCustomersCuc()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("CUC-001", Assert.Single(writer.Rows)[13].Text);
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
        Assert.Equal("Bogotá", row[12].Text);
        Assert.Equal("Calle 1 # 2-3", row[15].Text);
        Assert.Equal("3001234567", row[17].Text);
        Assert.Equal("cliente@ejemplo.co", row[18].Text);
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
        Assert.Equal("Rionegro", row[12].Text);
        Assert.Equal("Zona Franca, Bodega 14", row[15].Text);
        Assert.Equal("6015550000", row[17].Text);
        Assert.Equal("bodega@ejemplo.co", row[18].Text);
    }

    [Fact]
    public async Task PaymentDatesFillFromTheProofsUploadedAtInTheTenantsLocalTime()
    {
        var writer = new RecordingExportWorkbookWriter();
        var convertedAt = new DateTimeOffset(2026, 9, 12, 15, 0, 0, TimeSpan.Zero); // 10:00 en Bogotá.

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", publicKeys: ["a.pdf"], at: convertedAt)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("2026-09-12 10:00", row[7].Text);
        Assert.Equal(string.Empty, row[8].Text);
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
            Enumerable.Range(7, 5).Select(index => row[index].Text));
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

    // Spec 2026-09-24, D9: la columna nueva va después de Email, para no mover nada de lo que el
    // ERP ya importa. Banco, Cuenta y los comprobantes (2026-09-24) van después de ella.
    [Fact]
    public async Task CodAsesorFollowsEmailAndEveryRowFillsEveryColumn()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Email", writer.Columns[18].Header);
        Assert.Equal("Cod. Asesor", writer.Columns[19].Header);
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    // 2026-09-24: Banco y Cuenta salen de la cuenta de facturación congelada en la cotización, la
    // misma para todos los comprobantes del pedido, y se repiten en cada línea.
    [Fact]
    public async Task BancoAndCuentaComeFromTheBillingAccountOnEveryLine()
    {
        var writer = new RecordingExportWorkbookWriter();
        var billingAccount = new QuotationBillingAccount
        {
            CompanyId = CompanyId,
            BankName = "Bancolombia",
            AccountNumber = "123456789",
            Currency = "COP",
        };
        var row = NewRow(
            "PED-2026-0001",
            billingAccount: billingAccount,
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal("Bancolombia", cells[BancoIndex].Text);
            Assert.Equal("123456789", cells[BancoIndex + 1].Text);
        }
    }

    [Fact]
    public async Task BancoAndCuentaAreEmptyWithoutABillingAccount()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: null)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(string.Empty, row[BancoIndex].Text);
        Assert.Equal(string.Empty, row[BancoIndex + 1].Text);
    }

    // 2026-09-24: por comprobante, su monto como número y su enlace público clicable, con la URL
    // misma como texto para que se lea sin abrirla. Mismo orden que "Fecha Pago N".
    [Fact]
    public async Task EachProofWritesItsAmountAndItsPublicLink()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", proofs: [(60_000m, "payment-proofs/a.pdf"), (40_000m, "payment-proofs/b.png")]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(60_000m, cells[ProofIndex(1)].Number);
        Assert.Null(cells[ProofIndex(1)].Text);
        var firstUrl = $"{RecordingPaymentProofPublisher.BaseUrl}/payment-proofs/a.pdf";
        Assert.Equal(ExportCell.OfLink(firstUrl, firstUrl), cells[ProofIndex(1) + 1]);
        Assert.Equal(40_000m, cells[ProofIndex(2)].Number);
        var secondUrl = $"{RecordingPaymentProofPublisher.BaseUrl}/payment-proofs/b.png";
        Assert.Equal(ExportCell.OfLink(secondUrl, secondUrl), cells[ProofIndex(2) + 1]);
    }

    // Un comprobante privado (sin copia pública) sí existe: su monto sale igual, y el enlace dice
    // «Sin enlace» para no confundirse con la celda vacía de un comprobante que no hay.
    [Fact]
    public async Task APrivateProofKeepsItsAmountAndSaysSinEnlace()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", proofs: [(25_000m, null)]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(25_000m, cells[ProofIndex(1)].Number);
        Assert.Equal(ExportCell.OfText("Sin enlace"), cells[ProofIndex(1) + 1]);
    }

    // Con la opción de enlaces públicos apagada, UrlFor no da URL aunque la clave exista.
    [Fact]
    public async Task APublicProofSaysSinEnlaceWhenPublicLinksAreOff()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", proofs: [(25_000m, "payment-proofs/a.pdf")]);

        await NewProcessor(
                new StubOrderListRepository(row), writer, publisher: new RecordingPaymentProofPublisher(enabled: false))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("Sin enlace"), Assert.Single(writer.Rows)[ProofIndex(1) + 1]);
    }

    [Fact]
    public async Task ProofColumnsBeyondTheProofCountAreEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", proofs: [(10_000m, "payment-proofs/a.pdf")]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        foreach (var number in Enumerable.Range(2, OrdersExportProcessor.PaymentDateColumns - 1))
        {
            Assert.Equal(ExportCell.OfText(string.Empty), cells[ProofIndex(number)]);
            Assert.Equal(ExportCell.OfText(string.Empty), cells[ProofIndex(number) + 1]);
        }
    }

    // Banco va justo después de "Cod. Asesor"; Cuenta, después de Banco.
    private const int BancoIndex = 20;

    // "V. Comprobante N" (desde 1); su "URL Comprobante N" es la columna siguiente.
    private static int ProofIndex(int number) => BancoIndex + 2 + ((number - 1) * 2);

    // D9: celda numérica, repetida en cada línea del pedido como el resto de sus campos.
    [Fact]
    public async Task CodAsesorCarriesTheAdvisorsCodeOnEveryLine()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(
                new StubOrderListRepository(row),
                writer,
                advisors: new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: 12))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(12m, cells[19].Number);
            Assert.Null(cells[19].Text);
        }
    }

    // D9: vacía si la membresía asesora no tiene código.
    [Fact]
    public async Task CodAsesorIsEmptyWhenTheAdvisorHasNoCode()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                advisors: new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: null))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cell = Assert.Single(writer.Rows)[19];
        Assert.Equal(string.Empty, cell.Text);
        Assert.Null(cell.Number);
    }

    // Review Focus 5: una asesora que el lookup no devuelve (otro tenant, fila borrada) deja la
    // celda vacía, sin romper el lote ni correr las columnas.
    [Fact]
    public async Task CodAsesorIsEmptyWhenTheAdvisorDoesNotResolve()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                advisors: new StubQuotationAdvisorLookup(resolves: false))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, row.Count);
        Assert.Equal(string.Empty, row[19].Text);
        Assert.Null(row[19].Number);
    }

    // D9: una consulta por lote, con las asesoras distintas del lote — no una por pedido ni por
    // línea.
    [Fact]
    public async Task ResolvesTheAdvisorsOncePerBatchWithTheDistinctIds()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
            .ToArray();
        var advisors = new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: 12);

        await NewProcessor(new StubOrderListRepository(rows), advisors: advisors)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, advisors.FindCalls);
        Assert.All(advisors.Requests, request => Assert.Equal([AdvisorId.Value], request));
    }

    // Spec 2026-09-24 (homologación de columnas): sin fila guardada, el archivo es el de siempre,
    // y WritesTheErpColumnsInOrder lo sigue fijando encabezado por encabezado. Acá queda dicho
    // explícito, y que el layout se preguntó igual.
    [Fact]
    public async Task WithoutAStoredLayoutTheFileIsTheCatalogAsToday()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = new InMemoryOrdersExportLayoutRepository();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.Equal(1, layouts.FindCalls);
    }

    // D2 y D8: encabezados del tenant, su orden, sin las ocultas y con el resto del catálogo
    // detrás. Los anchos son los del catálogo, no importa el nombre.
    [Fact]
    public async Task AStoredLayoutRenamesReordersAndHidesColumns()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(32, writer.Columns.Count);
        Assert.Equal(new ExportColumn("Correo", 30), writer.Columns[0]);
        Assert.Equal(new ExportColumn("Pedido", 18), writer.Columns[1]);
        Assert.Equal(new ExportColumn("Cod. Producto", 18), writer.Columns[2]);
        Assert.Equal("Valor Unit sin IVA", writer.Columns[^1].Header);
        Assert.DoesNotContain(writer.Columns, column => column.Header == "EMPRESA");
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, cells.Count);
        Assert.Equal("cliente@ejemplo.co", cells[0].Text);
        Assert.Equal("PED-2026-0001", cells[1].Text);
        Assert.Equal("TOR-001", cells[2].Text);
        Assert.Equal(2m, cells[3].Number);
    }

    // D4 y Review Focus 3: la fija repite su texto en cada línea del pedido, también la vacía —una
    // celda vacía, no espacios—; ancho 18, en su posición.
    [Fact]
    public async Task FixedColumnsWriteTheirTextOnEveryRowIncludingAnEmptyOne()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Fixed("Bodega", "   ", visible: true));
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(35, writer.Columns.Count);
        Assert.Equal(new ExportColumn("Tipo Doc", OrdersExportLayoutProjection.FixedColumnWidth), writer.Columns[0]);
        Assert.Equal(new ExportColumn("EMPRESA", 30), writer.Columns[1]);
        Assert.Equal(new ExportColumn("Bodega", 18), writer.Columns[2]);
        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(writer.Columns.Count, cells.Count);
            Assert.Equal(ExportCell.OfText("FV"), cells[0]);
            Assert.Equal(ExportCell.OfText(string.Empty), cells[2]);
        }

        // Cantidad queda en la 5.ª: Tipo Doc, EMPRESA, Bodega, Cod. Producto, Cantidad.
        Assert.Equal(2m, writer.Rows[0][4].Number);
        Assert.Equal(5m, writer.Rows[1][4].Number);
    }

    // Una fija oculta no viaja: ni columna ni celda. Hay 33 columnas, como sin layout.
    [Fact]
    public async Task AHiddenFixedColumnIsNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: false));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.DoesNotContain(Assert.Single(writer.Rows), cell => cell.Text == "FV");
    }

    // Una fila con dos fijas seguidas y una columna del catálogo entre otras dos fijas: el orden
    // de las fijas es el del layout, no el de las llaves.
    [Fact]
    public async Task FixedColumnsComeOutInTheLayoutsOrderAmongTheCatalogOnes()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true),
            OrdersExportColumnSetting.Fixed("A", "a", visible: true),
            OrdersExportColumnSetting.Fixed("B", "b", visible: true),
            OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
            OrdersExportColumnSetting.Fixed("C", "c", visible: true));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(["Pedido", "A", "B", "Email", "C", "EMPRESA"], writer.Columns.Take(6).Select(column => column.Header));
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(["PED-2026-0001", "a", "b", "cliente@ejemplo.co", "c", string.Empty], cells.Take(6).Select(cell => cell.Text));
    }

    // El layout se resuelve una vez por job: no por lote ni por fila.
    [Fact]
    public async Task TheLayoutIsReadOnceForTheWholeJob()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
            .ToArray();
        var layouts = StoredLayout(OrdersExportColumnSetting.Catalog("email", "Correo", visible: true));

        await NewProcessor(new StubOrderListRepository(rows), layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(1, layouts.FindCalls);
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
        string? notes = null,
        IReadOnlyList<(decimal Amount, string? PublicKey)>? proofs = null)
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

        var proofInputs = (proofs ?? [.. (publicKeys ?? []).Select(publicKey => (10_000m, publicKey))])
            .Select(proof => new OrderPaymentProofInput(Guid.CreateVersion7(), proof.Amount, proof.PublicKey))
            .ToArray();
        var order = Order.Create(
            OrderId.New(), TenantId, orderNumber, quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, proofInputs, occurredAt);
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
        StubQuotationGeographyLookup? geography = null,
        StubQuotationAdvisorLookup? advisors = null,
        RecordingPaymentProofPublisher? publisher = null,
        InMemoryOrdersExportLayoutRepository? layouts = null) =>
        new(repository,
            // Sin layout guardado por defecto: el archivo de siempre (spec 2026-09-24, D8).
            layouts ?? new InMemoryOrdersExportLayoutRepository(),
            customers ?? new StubQuotationCustomerLookup(DefaultCustomer),
            products ?? new StubQuotationProductLookup(
                new Dictionary<Guid, QuotationProductRef>
                {
                    [ProductId] = DefaultProduct,
                    [OtherProductId] = DefaultProduct with { Id = OtherProductId, Code = "OTR-002" },
                }),
            companies ?? new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            geography ?? new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            publisher ?? new RecordingPaymentProofPublisher(),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));

    /// <summary>Un layout guardado para el tenant de las pruebas (spec 2026-09-24): lo que se
    /// pasa se completa con el catálogo, como hace Replace.</summary>
    private static InMemoryOrdersExportLayoutRepository StoredLayout(params OrdersExportColumnSetting[] columns)
    {
        var layouts = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        Assert.True(layout.Replace(columns, Now));
        layouts.Add(layout);
        return layouts;
    }
}
