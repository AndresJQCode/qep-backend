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
    private static readonly Guid CustomerCityId = Guid.CreateVersion7();
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
        Email: "cliente@ejemplo.co", CityName: "Bogotá", IdentificationNumber: "900123456");

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
                "Valor Unit sin IVA", "Fecha Pedido", "Cliente", "Documento de identidad",
                "Banco y cuenta", "Total consignado", "Tasa IVA", "NIT Empresa", "Total facturado",
                "Retencion",
            ],
            writer.Columns.Select(column => column.Header));
    }

    // Spec 2026-10-05, D7: la hoja se llama como diga el layout del tenant. Su importador la busca
    // por nombre ("MIGRACION 1") y antes había que renombrarla a mano en cada exportación.
    [Fact]
    public async Task TheSheetIsNamedAfterTheTenantsLayout()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        Assert.True(layout.Replace(OrdersExportLayout.Effective(stored: null), "MIGRACION 1", Now));
        layouts.Add(layout);

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("MIGRACION 1", writer.SheetName);
    }

    // 2026-09-24: "Valor Unit sin IVA" va al final, por la misma razón que "Cod. Asesor". Es el
    // precio unitario bruto con el IVA que trae adentro quitado, antes del descuento — distinto de
    // "Valor Unit", que desde 2026-10-03 lleva el neto (con descuento y sin IVA).
    [Fact]
    public async Task ValorUnitSinIvaFollowsTheProofsWithTheVatRemovedFromTheUnitPrice()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", items: [(ProductId, 3m, 119_000m, 10m, 19)]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Valor Unit sin IVA", writer.Columns[ValorUnitSinIvaIndex].Header);
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, cells.Count);
        Assert.Equal(100_000m, cells[ValorUnitSinIvaIndex].Number);
        Assert.Null(cells[ValorUnitSinIvaIndex].Text);
    }

    // 2026-10-03: "Valor Unit" es lo que el ERP cobra por unidad: con el descuento ya aplicado y sin
    // IVA, para que Cantidad × Valor Unit cuadre con el IVA y los totales del pedido. 3 × 119.000
    // con 10% de descuento cobra 321.300, que sin el 19% de IVA son 270.000: 90.000 por unidad.
    // "Descuento" sigue siendo el monto de la línea, informativo.
    [Fact]
    public async Task ValorUnitIsTheUnitPriceWithTheDiscountAppliedAndWithoutVat()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", items: [(ProductId, 3m, 119_000m, 10m, 19)]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Valor Unit", writer.Columns[ValorUnitIndex].Header);
        Assert.Equal("Descuento", writer.Columns[DescuentoIndex].Header);
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(90_000m, cells[ValorUnitIndex].Number);
        Assert.Null(cells[ValorUnitIndex].Text);
        Assert.Equal(35_700m, cells[DescuentoIndex].Number);
    }

    private const int ValorUnitIndex = 3;

    private const int DescuentoIndex = 5;

    private const int ValorUnitSinIvaIndex = 32;

    private const int FechaPedidoIndex = 33;

    private const int ClienteIndex = 34;

    private const int DocumentoIdentidadIndex = 35;

    // Ajuste 2026-09-25: "Fecha Pedido" es el día en que nació el pedido (Order.CreatedAt), en el
    // día del tenant y no en UTC — 22:00 de Bogotá ya es el día siguiente en UTC. Texto, como
    // "Fecha Pago N", porque el ERP lo importa tal cual.
    [Fact]
    public async Task FechaPedidoIsTheOrdersCreationDayInTheTenantsLocalTime()
    {
        var writer = new RecordingExportWorkbookWriter();
        var createdAt = new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero); // 22:00 del 11 en Bogotá.

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", at: createdAt)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Fecha Pedido", 14), writer.Columns[FechaPedidoIndex]);
        var cell = Assert.Single(writer.Rows)[FechaPedidoIndex];
        Assert.Equal(ExportCell.OfText("2026-09-11"), cell);
    }

    // Ajuste 2026-09-25: "Cliente" es a nombre de quién sale la factura, con la misma precedencia
    // que el PDF: consumidor final, la parte de facturación propia, la razón social si la
    // cotización factura a ella, y si no el nombre de contacto de la ficha.
    [Fact]
    public async Task ClienteIsTheCustomersNameWithoutABillingParty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Cliente", 30), writer.Columns[ClienteIndex]);
        Assert.Equal(ExportCell.OfText("Ferretería El Tornillo"), Assert.Single(writer.Rows)[ClienteIndex]);
    }

    [Fact]
    public async Task ClienteIsTheBillingPartysNameWhenTheQuotationHasOne()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: new QuotationPartyDetails
            {
                Name = "Distribuciones Andinas S.A.S.",
                IdentificationNumber = "901555444-1",
                Phone = "6015550000",
            },
            Shipping: null,
            BillingWithRetention: false,
            BillingVatSurplus: false);

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Distribuciones Andinas S.A.S.", Assert.Single(writer.Rows)[ClienteIndex].Text);
    }

    [Fact]
    public async Task ClienteIsTheBusinessNameWhenTheQuotationBillsToIt()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { BusinessName = "Tornillos del Valle S.A.S." });
        var parties = QuotationParties.Empty with { BillingUsesBusinessName = true };

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)),
                writer,
                customers: customers)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Tornillos del Valle S.A.S.", Assert.Single(writer.Rows)[ClienteIndex].Text);
    }

    // Una razón social pedida pero vacía en la ficha no deja la celda en blanco: cae al contacto.
    [Fact]
    public async Task ClienteFallsBackToTheContactNameWhenTheBusinessNameIsMissing()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = QuotationParties.Empty with { BillingUsesBusinessName = true };

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Ferretería El Tornillo", Assert.Single(writer.Rows)[ClienteIndex].Text);
    }

    [Fact]
    public async Task ClienteIsFinalConsumerWhenTheQuotationBillsToIt()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = QuotationParties.Empty with { BillsToFinalConsumer = true };

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(FinalConsumer.Name, Assert.Single(writer.Rows)[ClienteIndex].Text);
    }

    // Un cliente que el lookup no devuelve deja la celda vacía, sin correr las columnas.
    [Fact]
    public async Task ClienteIsEmptyWhenTheCustomerDoesNotResolve()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer);
        customers.Refs.Clear();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                customers: customers)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, row.Count);
        Assert.Equal(ExportCell.OfText(string.Empty), row[ClienteIndex]);
    }

    // Ajuste 2026-09-26: "Documento de identidad" es el número de documento de la misma persona que
    // nombra "Cliente", con la misma precedencia. "Documento" sigue siendo el CUC.
    [Fact]
    public async Task DocumentoIdentidadIsTheCustomersIdentificationNumberWithoutABillingParty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Documento de identidad", 18), writer.Columns[DocumentoIdentidadIndex]);
        var row = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText("900123456"), row[DocumentoIdentidadIndex]);
        Assert.Equal("CUC-001", row[13].Text);
    }

    // La razón social es del mismo cliente: su NIT es el de la ficha.
    [Fact]
    public async Task DocumentoIdentidadIsTheCustomersNumberWhenTheQuotationBillsToTheBusinessName()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { BusinessName = "Tornillos del Valle S.A.S." });
        var parties = QuotationParties.Empty with { BillingUsesBusinessName = true };

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)),
                writer,
                customers: customers)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("900123456", Assert.Single(writer.Rows)[DocumentoIdentidadIndex].Text);
    }

    [Fact]
    public async Task DocumentoIdentidadIsTheFinalConsumersGenericNumber()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = QuotationParties.Empty with { BillsToFinalConsumer = true };

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(FinalConsumer.IdentificationNumber, Assert.Single(writer.Rows)[DocumentoIdentidadIndex].Text);
    }

    // Una parte de facturación con nombre propio es otra persona, y desde 2026-09-26 trae su
    // propio documento: la celda sale con ése y no con el del cliente.
    [Fact]
    public async Task DocumentoIdentidadIsTheBillingPartysNumberWhenItNamesSomeoneElse()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: new QuotationPartyDetails
            {
                Name = "Distribuciones Andinas S.A.S.",
                IdentificationNumber = "901555444-1",
                Phone = "6015550000",
            },
            Shipping: null,
            BillingWithRetention: false,
            BillingVatSurplus: false);

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("901555444-1"), Assert.Single(writer.Rows)[DocumentoIdentidadIndex]);
    }

    // Una parte guardada antes de que existiera el número tiene nombre y no documento: la celda
    // queda vacía en vez de ponerle el del cliente, que es otra persona.
    [Fact]
    public async Task DocumentoIdentidadIsEmptyWhenALegacyBillingPartyNamesSomeoneElseWithoutANumber()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: new QuotationPartyDetails
            {
                Name = "Distribuciones Andinas S.A.S.",
                IdentificationNumber = "901555444-1",
                Phone = "6015550000",
            },
            Shipping: null,
            BillingWithRetention: false,
            BillingVatSurplus: false);
        var row = NewRow("PED-2026-0001", parties: parties);
        typeof(QuotationParty)
            .GetProperty(nameof(QuotationParty.IdentificationNumber))!
            .SetValue(row.Quotation.Billing, null);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText(string.Empty), Assert.Single(writer.Rows)[DocumentoIdentidadIndex]);
    }

    // Una parte de facturación sin nombre deja "Cliente" en el de la ficha, y con él su documento.
    [Fact]
    public async Task DocumentoIdentidadIsTheCustomersNumberWhenTheBillingPartyHasNoName()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: new QuotationPartyDetails { Phone = "6015550000" },
            Shipping: null,
            BillingWithRetention: false,
            BillingVatSurplus: false);

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("Ferretería El Tornillo", row[ClienteIndex].Text);
        Assert.Equal("900123456", row[DocumentoIdentidadIndex].Text);
    }

    [Fact]
    public async Task DocumentoIdentidadIsEmptyWhenTheCustomerDoesNotResolve()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer);
        customers.Refs.Clear();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                customers: customers)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText(string.Empty), Assert.Single(writer.Rows)[DocumentoIdentidadIndex]);
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

    // Ajuste 2026-10-02: "Ciudad Coordinadora" sigue la misma precedencia que "Ciudad" (ContactFor):
    // con parte de entrega, la ciudad de la parte; sin ella, la del cliente. Pero el nombre es el de
    // Coordinadora, nunca el del DANE.
    [Fact]
    public async Task CiudadCoordinadoraIsTheShippingPartysCityAsCoordinadoraNamesIt()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails { Name = "Bodega Norte", CityId = CityId });
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { CityId = CustomerCityId });
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string> { [CityId] = "Rionegro", [CustomerCityId] = "Abejorral" },
            new Dictionary<Guid, string> { [CityId] = "RIONEGRO (ANT)", [CustomerCityId] = "ABEJORRAL (ANT)" });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)),
                writer,
                customers: customers,
                geography: geography,
                layouts: CoordinadoraCityVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Ciudad Coordinadora", writer.Columns[0].Header);
        Assert.Equal("RIONEGRO (ANT)", Assert.Single(writer.Rows)[0].Text);
    }

    [Fact]
    public async Task CiudadCoordinadoraFallsBackToTheCustomersCityWithoutAShippingParty()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { CityId = CustomerCityId });
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string> { [CustomerCityId] = "ABEJORRAL (ANT)" });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: QuotationParties.Empty)),
                writer,
                customers: customers,
                geography: geography,
                layouts: CoordinadoraCityVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("ABEJORRAL (ANT)", Assert.Single(writer.Rows)[0].Text);
    }

    // Un municipio que Coordinadora no lista, o un cliente sin ciudad (extranjero): vacía. Nunca el
    // nombre del DANE, que la transportadora no reconoce.
    [Fact]
    public async Task CiudadCoordinadoraIsEmptyWhenTheCityHasNoCoordinadoraName()
    {
        var writer = new RecordingExportWorkbookWriter();
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { CityId = CustomerCityId });
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string> { [CustomerCityId] = "Altos del Rosario" });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: QuotationParties.Empty)),
                writer,
                customers: customers,
                geography: geography,
                layouts: CoordinadoraCityVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[0].Text);
    }

    // La parte de entrega manda aunque no tenga ciudad: igual que "Ciudad", no cae a la del cliente.
    [Fact]
    public async Task CiudadCoordinadoraIsEmptyWhenTheShippingPartyHasNoCity()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails { Name = "Bodega Norte", Address = "Zona Franca" });
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { CityId = CustomerCityId });
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string> { [CustomerCityId] = "ABEJORRAL (ANT)" });

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)),
                writer,
                customers: customers,
                geography: geography,
                layouts: CoordinadoraCityVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(writer.Rows)[0].Text);
    }

    // Las ciudades de entrega y las de los clientes del lote en una sola consulta.
    [Fact]
    public async Task ResolvesTheCoordinadoraNamesOncePerBatchWithShippingAndCustomerCities()
    {
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails { Name = "Bodega Norte", CityId = CityId });
        var customers = new StubQuotationCustomerLookup(DefaultCustomer with { CityId = CustomerCityId });
        var geography = new StubQuotationGeographyLookup(new Dictionary<Guid, string>());

        await NewProcessor(
                new StubOrderListRepository(
                    NewRow("PED-2026-0001", parties: parties),
                    NewRow("PED-2026-0002", parties: QuotationParties.Empty)),
                customers: customers,
                geography: geography)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(1, geography.FindCoordinadoraCityNamesCalls);
        Assert.Equal(
            new[] { CityId, CustomerCityId }.Order(),
            geography.LastCoordinadoraRequestedIds.Order());
    }

    // Oculta por defecto: el Excel de un tenant que no la prende no cambia.
    [Fact]
    public async Task WithoutALayoutThatShowsItCiudadCoordinadoraIsNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(writer.Columns, column => column.Header == "Ciudad Coordinadora");
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    // La columna nueva va oculta al final del efectivo: un layout que la pide visible la pone donde
    // la guarda, acá de primera, para leerla en la celda 0.
    private static InMemoryOrdersExportLayoutRepository CoordinadoraCityVisibleFirst() =>
        StoredLayout(OrdersExportColumnSetting.Catalog("coordinadora_city", "Ciudad Coordinadora", visible: true));

    // Spec 2026-10-05 (recoger en tienda): "Transportadora" sale por pedido desde la cotización.
    // Hasta entonces la hoja del tenant la tenía fija en "Coordinadora", así que quien despachaba no
    // se enteraba de que el cliente pasaba a recoger. Los dos textos son contrato del ERP del tenant
    // y se fijan literales: cambiar la constante tiene que romper estas pruebas.
    [Fact]
    public async Task TransportadoraIsRecogerEnTiendaWhenTheQuotationIsAStorePickup()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: StorePickup)),
                writer,
                layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Transportadora", 20), writer.Columns[0]);
        Assert.Equal(ExportCell.OfText("Recoger en tienda"), Assert.Single(writer.Rows)[0]);
    }

    [Fact]
    public async Task TransportadoraIsCoordinadoraWhenTheQuotationIsNotAStorePickup()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", parties: QuotationParties.Empty)),
                writer,
                layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("Coordinadora"), Assert.Single(writer.Rows)[0]);
    }

    // Review Focus 1: es un dato del pedido, como "Pedido" o "Direccion", así que se repite en cada
    // una de sus líneas.
    [Fact]
    public async Task TransportadoraRepeatsOnEveryLineOfAStorePickupOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            parties: StorePickup,
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: CarrierVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        Assert.All(writer.Rows, cells => Assert.Equal(ExportCell.OfText("Recoger en tienda"), cells[0]));
    }

    // Review Focus 2: un pedido de recogida y uno con envío en el mismo lote. Cada fila con lo suyo:
    // un valor calculado una vez por lote arrastraría "Recoger en tienda" al otro pedido.
    [Fact]
    public async Task TransportadoraIsDecidedPerOrderWithinTheSameBatch()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true),
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true));

        await NewProcessor(
                new StubOrderListRepository(
                    NewRow("PED-2026-0001", parties: StorePickup),
                    NewRow("PED-2026-0002", parties: QuotationParties.Empty)),
                writer,
                layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PED-2026-0001"] = "Recoger en tienda",
                ["PED-2026-0002"] = "Coordinadora",
            },
            writer.Rows.ToDictionary(cells => cells[1].Text!, cells => cells[0].Text!));
    }

    // Review Focus 3, decisión del owner (2026-10-05): la recogida sólo cambia "Transportadora". El
    // dominio descarta la parte de entrega que llegue con la recogida, así que "Direccion" cae a la
    // del cliente, como en cualquier pedido sin parte de entrega propia. No se vacía.
    [Fact]
    public async Task RecogerEnTiendaKeepsTheCustomersAddressInDireccion()
    {
        var writer = new RecordingExportWorkbookWriter();
        var parties = new QuotationParties(
            Billing: null,
            Shipping: new QuotationPartyDetails { Name = "Bodega Norte", Address = "Zona Franca, Bodega 14" },
            IsStorePickup: true);
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true),
            OrdersExportColumnSetting.Catalog("address", "Direccion", visible: true));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: parties)), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText("Recoger en tienda"), cells[0]);
        Assert.Equal(ExportCell.OfText("Calle 1 # 2-3"), cells[1]);
    }

    // Review Focus 4: oculta por defecto. El Excel de un tenant que no la prende no cambia: ni
    // encabezado nuevo ni celda de más.
    [Fact]
    public async Task WithoutALayoutThatShowsItTransportadoraIsNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", parties: StorePickup)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.DoesNotContain(writer.Columns, column => column.Header == "Transportadora");
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    private static readonly QuotationParties StorePickup = new(Billing: null, Shipping: null, IsStorePickup: true);

    // La columna nueva va oculta al final del efectivo: un layout que la pide visible la pone donde
    // la guarda, acá de primera, para leerla en la celda 0.
    private static InMemoryOrdersExportLayoutRepository CarrierVisibleFirst() =>
        StoredLayout(OrdersExportColumnSetting.Catalog("carrier", "Transportadora", visible: true));

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

    // A pedido (2026-10-08): si el comprobante trae su fecha de pago, «Fecha Pago N» es esa fecha
    // —la del soporte, sin hora, porque es lo que escribió quien lo adjuntó— y no el instante en
    // que se subió. Sin ella (comprobantes anteriores al campo) sigue cayendo a la fecha de subida.
    [Fact]
    public async Task PaymentDatesPreferTheProofsPaidOnDateOverTheUploadInstant()
    {
        var writer = new RecordingExportWorkbookWriter();
        var convertedAt = new DateTimeOffset(2026, 9, 12, 15, 0, 0, TimeSpan.Zero); // 10:00 en Bogotá.

        await NewProcessor(
                new StubOrderListRepository(NewRow(
                    "PED-2026-0001",
                    proofs: [(10_000m, null), (5_000m, null)],
                    paidOn: [new DateOnly(2026, 9, 10), null],
                    at: convertedAt)),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("2026-09-10", row[7].Text);
        Assert.Equal("2026-09-12 10:00", row[8].Text);
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

    // Ajuste 2026-09-26: "Banco y cuenta" junta en una celda lo que "Banco" y "Cuenta" llevan por
    // separado, con un solo espacio en medio, en cada línea del pedido.
    [Fact]
    public async Task BancoYCuentaJoinsTheBankAndTheAccountNumberWithASpace()
    {
        var writer = new RecordingExportWorkbookWriter();
        var billingAccount = new QuotationBillingAccount
        {
            CompanyId = CompanyId,
            BankName = "BANCOLOMBIA",
            AccountNumber = "7542",
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

        Assert.Equal(new ExportColumn("Banco y cuenta", 36), writer.Columns[BancoYCuentaIndex]);
        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(ExportCell.OfText("BANCOLOMBIA 7542"), cells[BancoYCuentaIndex]);
            // "Banco" y "Cuenta" no cambian: otros ERP las leen por separado.
            Assert.Equal("BANCOLOMBIA", cells[BancoIndex].Text);
            Assert.Equal("7542", cells[BancoIndex + 1].Text);
        }
    }

    [Fact]
    public async Task BancoYCuentaIsEmptyWithoutABillingAccount()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: null)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText(string.Empty), Assert.Single(writer.Rows)[BancoYCuentaIndex]);
    }

    // Ajuste 2026-09-26: "Total consignado" suma TODOS los comprobantes del pedido, no sólo los cinco
    // que tienen columna propia, como número —el ERP lo suma—, y se repite en cada línea.
    [Fact]
    public async Task TotalConsignadoSumsEveryProofOfTheOrderBeyondTheFiveWithTheirOwnColumn()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            proofs:
            [
                (10_000m, "payment-proofs/a.pdf"),
                (20_000m, null),
                (30_000m, "payment-proofs/c.pdf"),
                (40_000m, null),
                (50_000m, "payment-proofs/e.pdf"),
                (60_000m, null),
            ],
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Total consignado", 18), writer.Columns[TotalConsignadoIndex]);
        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(ExportCell.OfNumber(210_000m), cells[TotalConsignadoIndex]);
            // "V. Comprobante 1" sigue siendo sólo el primero.
            Assert.Equal(10_000m, cells[ProofIndex(1)].Number);
        }
    }

    // Ajuste 2026-10-05: "Total facturado" es Quotation.Total —Subtotal + IVA, con el descuento de
    // cada línea ya adentro—, el mismo en cada línea del pedido. Visible por defecto; desde el ajuste
    // 2026-10-06 la sigue "Retencion".
    private const int TotalFacturadoIndex = 40;

    [Fact]
    public async Task TotalFacturadoIsTheOrdersTotalRepeatedOnEveryLine()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 10m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        // 2 × 1.000 con 10 % de descuento son 1.800, más 5 × 500: 4.300 con el IVA adentro. Ya neto
        // del descuento de la línea: restarle DiscountAmount lo descontaría dos veces.
        Assert.Equal(4_300m, row.Quotation.Total);
        Assert.Equal(new ExportColumn("Total facturado", 18), writer.Columns[TotalFacturadoIndex]);
        Assert.Equal(2, writer.Rows.Count);
        Assert.All(writer.Rows, cells => Assert.Equal(ExportCell.OfNumber(4_300m), cells[TotalFacturadoIndex]));
    }

    // Con retención en la fuente el cliente paga NetTotal, pero lo facturado sigue siendo Total: la
    // retención no es un descuento de la factura.
    [Fact]
    public async Task TotalFacturadoIsTheTotalAndNotTheNetTotalWhenTheOrderHasRetention()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", customerWithRetention: true);
        Assert.True(row.Quotation.RetentionAmount > 0m);
        Assert.NotEqual(row.Quotation.Total, row.Quotation.NetTotal);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfNumber(row.Quotation.Total), Assert.Single(writer.Rows)[TotalFacturadoIndex]);
    }

    // Visible por defecto (decisión del owner, 2026-10-05): también la gana un layout guardado que
    // no la nombra, al final, detrás de todo lo que el tenant ya tenía. Desde el ajuste 2026-10-06,
    // penúltima: "Retencion" va detrás.
    [Fact]
    public async Task AStoredLayoutThatDoesNotNameItGetsTotalFacturadoAtTheEnd()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001");
        var layouts = StoredLayout(OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true));

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Total facturado", 18), writer.Columns[^2]);
        Assert.Equal(ExportCell.OfNumber(row.Quotation.Total), Assert.Single(writer.Rows)[^2]);
    }

    // Ajuste 2026-10-06: "Retencion" es Quotation.RetentionAmount, la misma en cada línea del pedido,
    // detrás de "Total facturado". Con las dos el ERP cuadra lo facturado contra lo que de verdad se
    // cobra: NetTotal = Total − RetentionAmount. Visible por defecto, la última del archivo sin
    // layout guardado.
    private const int RetencionIndex = 41;

    [Fact]
    public async Task RetencionIsTheOrdersRetentionRepeatedOnEveryLine()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 10m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ],
            customerWithRetention: true);
        Assert.True(row.Quotation.RetentionAmount > 0m);
        Assert.Equal(row.Quotation.Total - row.Quotation.RetentionAmount, row.Quotation.NetTotal);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Retencion", 16), writer.Columns[RetencionIndex]);
        Assert.Equal(RetencionIndex, writer.Columns.Count - 1);
        Assert.Equal(2, writer.Rows.Count);
        Assert.All(writer.Rows, cells =>
        {
            Assert.Equal(ExportCell.OfNumber(row.Quotation.RetentionAmount), cells[RetencionIndex]);
            Assert.Equal(ExportCell.OfNumber(row.Quotation.Total), cells[TotalFacturadoIndex]);
        });
    }

    // Sin retención la celda es 0, un número y no vacía: a diferencia de "Total consignado", donde
    // vacío dice "no hay comprobante", acá 0 dice la verdad —no se retiene nada— y el ERP lo resta
    // igual.
    [Fact]
    public async Task RetencionIsZeroWhenTheOrderHasNoRetention()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001");
        Assert.Equal(0m, row.Quotation.RetentionAmount);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfNumber(0m), Assert.Single(writer.Rows)[RetencionIndex]);
    }

    // Visible por defecto, como "Total facturado": también la gana un layout guardado que no la
    // nombra, al final, detrás de todo lo que el tenant ya tenía.
    [Fact]
    public async Task AStoredLayoutThatDoesNotNameItGetsRetencionAtTheEnd()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001", customerWithRetention: true);
        var layouts = StoredLayout(OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true));

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Retencion", 16), writer.Columns[^1]);
        Assert.Equal(ExportCell.OfNumber(row.Quotation.RetentionAmount), Assert.Single(writer.Rows)[^1]);
    }

    // Sin comprobantes la celda queda vacía, igual que "V. Comprobante N" sin comprobante: vacío
    // dice "no hay comprobante", y un 0 diría que hubo una consignación de cero pesos.
    [Fact]
    public async Task TotalConsignadoIsEmptyWhenTheOrderHasNoProofs()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText(string.Empty), cells[TotalConsignadoIndex]);
        Assert.Equal(cells[ProofIndex(1)], cells[TotalConsignadoIndex]);
    }

    private const int BancoYCuentaIndex = 36;

    private const int TotalConsignadoIndex = 37;

    private const int TasaIvaIndex = 38;

    // Ajuste 2026-09-26: la tasa de IVA de cada línea como fracción —19 % sale 0,19—, número y no
    // texto, y la de la línea: es la foto que QuotationItem tomó del producto al agregarla.
    [Fact]
    public async Task TasaIvaIsEachLinesTaxRateSnapshotAsAFraction()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 0),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("Tasa IVA", 12), writer.Columns[TasaIvaIndex]);
        Assert.Equal(2, writer.Rows.Count);
        Assert.Equal(ExportCell.OfNumber(0.19m), writer.Rows[0][TasaIvaIndex]);
        Assert.Equal(ExportCell.OfNumber(0m), writer.Rows[1][TasaIvaIndex]);
    }

    private const int NitEmpresaIndex = 39;

    // Ajuste 2026-09-26: "NIT Empresa" es el NIT de la empresa por la que se factura, la misma que
    // nombra "EMPRESA". Un NIT sólo de dígitos sale como número, igual que una fija numérica.
    [Fact]
    public async Task NitEmpresaIsTheBillingCompanysTaxIdAsANumberWhenItIsOnlyDigits()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: BillingAccountOfCompany())),
                writer,
                companies: CompanyWithTaxId("901851609"))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(new ExportColumn("NIT Empresa", 18), writer.Columns[NitEmpresaIndex]);
        Assert.Equal(ExportCell.OfNumber(901851609m), Assert.Single(writer.Rows)[NitEmpresaIndex]);
    }

    // Ajuste 2026-09-28: el ERP espera el NIT sin puntos, sin espacios y sin dígito de
    // verificación. Se limpia sólo al exportar; la empresa conserva el NIT como se escribió.
    [Theory]
    [InlineData("901851609-1")]
    [InlineData("901.851.609-1")]
    [InlineData("901 851 609")]
    [InlineData(" 901.851 609 - 1 ")]
    public async Task NitEmpresaDropsDotsSpacesAndTheCheckDigit(string taxId)
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: BillingAccountOfCompany())),
                writer,
                companies: CompanyWithTaxId(taxId))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfNumber(901851609m), Assert.Single(writer.Rows)[NitEmpresaIndex]);
    }

    // Sin cuenta de facturación, o con una empresa que no resuelve, vacía — como "EMPRESA".
    [Fact]
    public async Task NitEmpresaIsEmptyWithoutABillingCompany()
    {
        var writer = new RecordingExportWorkbookWriter();
        var rows = new[]
        {
            NewRow("PED-2026-0001"),
            NewRow("PED-2026-0002", billingAccount: BillingAccountOfCompany()),
        };

        await NewProcessor(
                new StubOrderListRepository(rows),
                writer,
                companies: new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        Assert.All(writer.Rows, cells => Assert.Equal(ExportCell.OfText(string.Empty), cells[NitEmpresaIndex]));
    }

    // Ajuste 2026-10-03: "Forma de pago N" lleva el banco con la cuenta sólo si el pedido tiene el
    // comprobante N. "bank_account" es del pedido, así que repetida bajo "Forma de pago 1" y
    // "Forma de pago 2" llenaba las dos aunque hubiera un solo comprobante. Las dos de primeras,
    // para leerlas en las celdas 0 y 1.
    private static InMemoryOrdersExportLayoutRepository PaymentMethodsVisibleFirst() =>
        StoredLayout(
            OrdersExportColumnSetting.Catalog("payment_method_1", "Forma de pago 1", visible: true),
            OrdersExportColumnSetting.Catalog("payment_method_2", "Forma de pago 2", visible: true));

    private static readonly QuotationBillingAccount Bancolombia7542 = new()
    {
        CompanyId = CompanyId,
        BankName = "BANCOLOMBIA",
        AccountNumber = "7542",
        Currency = "COP",
    };

    [Fact]
    public async Task FormaDePagoTwoIsEmptyWhenTheOrderHasASingleProof()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            billingAccount: Bancolombia7542,
            proofs: [(10_000m, "payment-proofs/a.pdf")],
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: PaymentMethodsVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExportColumn[] { new("Forma de pago 1", 36), new("Forma de pago 2", 36) },
            writer.Columns.Take(2));
        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(ExportCell.OfText("BANCOLOMBIA 7542"), cells[0]);
            Assert.Equal(ExportCell.OfText(string.Empty), cells[1]);
        }
    }

    [Fact]
    public async Task FormaDePagoIsEmptyEverywhereWhenTheOrderHasNoProofs()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", billingAccount: Bancolombia7542)),
                writer,
                layouts: PaymentMethodsVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText(string.Empty), cells[0]);
        Assert.Equal(ExportCell.OfText(string.Empty), cells[1]);
    }

    [Fact]
    public async Task FormaDePagoFillsOnePerProofWhenTheOrderHasTwo()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            billingAccount: Bancolombia7542,
            proofs: [(10_000m, "payment-proofs/a.pdf"), (20_000m, null)]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: PaymentMethodsVisibleFirst())
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cells = Assert.Single(writer.Rows);
        Assert.Equal(ExportCell.OfText("BANCOLOMBIA 7542"), cells[0]);
        Assert.Equal(ExportCell.OfText("BANCOLOMBIA 7542"), cells[1]);
    }

    // Ocultas por defecto: el Excel de un tenant que no las prende no cambia.
    [Fact]
    public async Task WithoutALayoutThatShowsThemFormaDePagoColumnsAreNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", proofs: [(10_000m, "payment-proofs/a.pdf")])),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(writer.Columns, column => column.Header.StartsWith("Forma de pago", StringComparison.Ordinal));
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    private static QuotationBillingAccount BillingAccountOfCompany() => new()
    {
        CompanyId = CompanyId,
        BankName = "Bancolombia",
        AccountNumber = "123",
        Currency = "COP",
    };

    private static StubQuotationCompanyLookup CompanyWithTaxId(string taxId) =>
        new(new Dictionary<Guid, QuotationCompanyRef>
        {
            [CompanyId] = new(CompanyId, "Raíces Orgánicas", taxId, IsActive: true, Address: null, Phone: null, BankAccounts: []),
        });

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

        Assert.Equal(41, writer.Columns.Count);
        Assert.Equal(new ExportColumn("Correo", 30), writer.Columns[0]);
        Assert.Equal(new ExportColumn("Pedido", 18), writer.Columns[1]);
        Assert.Equal(new ExportColumn("Cod. Producto", 18), writer.Columns[2]);
        Assert.Equal("Retencion", writer.Columns[^1].Header);
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

        Assert.Equal(44, writer.Columns.Count);
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

    // Una fija oculta no viaja: ni columna ni celda. Hay 42 columnas, como sin layout.
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

    // Ajuste 2026-09-25: una llave repetida escribe la misma celda bajo cada uno de sus encabezados
    // — el ERP del tenant lee la fecha del pedido en FECHA, Bloq/act y Vencimiento.
    [Fact]
    public async Task ARepeatedKeyWritesTheSameCellUnderEachOfItsHeaders()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("order_date", "FECHA", visible: true),
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true),
            OrdersExportColumnSetting.Catalog("order_date", "Bloq/act", visible: true),
            OrdersExportColumnSetting.Catalog("order_date", "Vencimiento", visible: true));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExportColumn[] { new("FECHA", 14), new("Pedido", 18), new("Bloq/act", 14), new("Vencimiento", 14) },
            writer.Columns.Take(4));
        Assert.DoesNotContain(writer.Columns, column => column.Header == "Fecha Pedido");
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, cells.Count);
        Assert.Equal(["2026-09-12", "PED-2026-0001", "2026-09-12", "2026-09-12"], cells.Take(4).Select(cell => cell.Text));
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

    // Owner, 2026-09-26: la línea de un pedido lleva el código que tenía el producto cuando la
    // cotización salió del borrador. Que el catálogo lo cambie después no cambia lo que va al ERP.
    [Fact]
    public async Task CodProductoIsTheSnapshotEvenIfTheCatalogChanged()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow("PED-2026-0001");
        row.Quotation.CaptureProductSnapshotsAfterConversion(new Dictionary<Guid, QuotationProductSnapshot>
        {
            [ProductId] = new("TOR-VIEJO", "Tornillo viejo"),
        });

        await NewProcessor(new StubOrderListRepository(row), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("TOR-VIEJO"), Assert.Single(writer.Rows)[1]);
    }

    // Una línea sin snapshot —enviada antes de que existiera— se sigue leyendo en vivo.
    [Fact]
    public async Task CodProductoFallsBackToTheCatalogWithoutASnapshot()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(ExportCell.OfText("TOR-001"), Assert.Single(writer.Rows)[1]);
    }

    // Si todas las líneas del lote ya están congeladas, el catálogo no tiene nada que aportar.
    [Fact]
    public async Task ALotOfFrozenLinesDoesNotReadTheCatalog()
    {
        var row = NewRow("PED-2026-0001");
        row.Quotation.CaptureProductSnapshotsAfterConversion(new Dictionary<Guid, QuotationProductSnapshot>
        {
            [ProductId] = new("TOR-VIEJO", "Tornillo viejo"),
        });
        var products = new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef>
        {
            [ProductId] = DefaultProduct,
        });

        await NewProcessor(new StubOrderListRepository(row), products: products)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(0, products.FindManyCalls);
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
        IReadOnlyList<(decimal Amount, string? PublicKey)>? proofs = null,
        bool customerWithRetention = false,
        // La fecha de pago de cada comprobante, por índice contra `proofs`; null o ausente deja el
        // comprobante sin fecha, como uno anterior al campo (2026-10-08).
        IReadOnlyList<DateOnly?>? paidOn = null)
    {
        var occurredAt = at ?? Now;
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes, parties ?? QuotationParties.Empty, billingAccount,
            QuotationCurrency.Cop,
            customerWithRetention, customerVatSurplus: false, AdvisorId, occurredAt);

        foreach (var item in items ?? DefaultItems)
        {
            quotation.AddItemAfterConversion(
                QuotationItemId.New(), item.ProductId, item.Quantity, item.UnitPrice,
                item.DiscountPercentage, item.TaxPercentage, AdvisorId, occurredAt);
        }

        var proofInputs = (proofs ?? [.. (publicKeys ?? []).Select(publicKey => (10_000m, publicKey))])
            .Select((proof, index) => new OrderPaymentProofInput(
                Guid.CreateVersion7(), proof.Amount, proof.PublicKey, paidOn?.ElementAtOrDefault(index)))
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
