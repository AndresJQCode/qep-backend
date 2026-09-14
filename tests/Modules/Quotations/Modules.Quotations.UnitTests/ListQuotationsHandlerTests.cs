using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// US-8: cada fila del listado muestra el cliente. El nombre viaja en la fila y no lo resuelve
/// quien consume el listado: hacerlo del otro lado es un GET por fila contra Customers.
/// </summary>
public sealed class ListQuotationsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid OtherClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    [Fact]
    public async Task ListPutsTheCustomerNameOnEachRow()
    {
        var customers = NewCustomerLookup();
        customers.Names[OtherClientId] = "Distribuidora del Sur";
        var handler = NewHandler(
            customers,
            NewQuotation("QUO-2026-0001", ClientId),
            NewQuotation("QUO-2026-0002", OtherClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(
            "Ferretería El Tornillo",
            Assert.Single(page.Items, item => item.ClientId == ClientId).ClientName);
        Assert.Equal(
            "Distribuidora del Sur",
            Assert.Single(page.Items, item => item.ClientId == OtherClientId).ClientName);
    }

    // Una sola ida por página, con los ids sin repetir: la alternativa —una consulta por fila—
    // es exactamente el N+1 que este campo existe para evitar.
    [Fact]
    public async Task ListResolvesEveryCustomerNameInASingleLookup()
    {
        var customers = NewCustomerLookup();
        customers.Names[OtherClientId] = "Distribuidora del Sur";
        var handler = NewHandler(
            customers,
            NewQuotation("QUO-2026-0001", ClientId),
            NewQuotation("QUO-2026-0002", ClientId),
            NewQuotation("QUO-2026-0003", OtherClientId));

        await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1, customers.FindNamesCalls);
        Assert.Equal(2, customers.LastRequestedIds.Count);
    }

    // El cliente es una referencia blanda (Quotation guarda el id, no una FK a otro módulo):
    // si no se resuelve, la fila sigue viajando y quien la muestra decide el respaldo.
    [Fact]
    public async Task ListLeavesTheNameNullWhenTheCustomerDoesNotResolve()
    {
        var handler = NewHandler(
            NewCustomerLookup(), NewQuotation("QUO-2026-0002", OtherClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).ClientName);
    }

    // La grilla presenta a la asesora por su nombre (spec 2026-09-11, D1, nota del 2026-09-14).
    [Fact]
    public async Task ListCarriesTheAdvisorNameWhenTheMemberHasOne()
    {
        var handler = NewHandler(NewCustomerLookup(), NewQuotation("QUO-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("Asesora Uno", Assert.Single(page.Items).AdvisorName);
    }

    // El owner y los miembros sembrados nacen con CreateActive, sin nombre: la fila cae al correo
    // en vez de viajar vacía, así que la pantalla no tiene que conocer dos campos para elegir.
    [Fact]
    public async Task ListFallsBackToTheAdvisorEmailWhenTheMemberHasNoName()
    {
        var handler = NewHandler(
            NewCustomerLookup(),
            new StubQuotationAdvisorLookup("asesora@qcode.co"),
            NewQuotation("QUO-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(page.Items).AdvisorName);
    }

    // La asesora es una referencia blanda, igual que el cliente: si la membresía no resuelve, la
    // fila viaja igual y sin etiqueta.
    [Fact]
    public async Task ListLeavesTheAdvisorNameNullWhenTheMemberDoesNotResolve()
    {
        var handler = NewHandler(
            NewCustomerLookup(),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", resolves: false),
            NewQuotation("QUO-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).AdvisorName);
    }

    // Las tres acciones que la fila ofrece y que no se deducen de su estado —editar, anular e
    // ir a aprobar el pedido— dependen de si ya se convirtio. Sin esto, quien pinta el listado
    // tiene que pedir el pedido de cada fila para saber cuales habilitar.
    [Fact]
    public async Task ListCarriesTheOrderOfEachConvertedQuotation()
    {
        var converted = NewQuotation("QUO-2026-0001", ClientId);
        var order = NewOrder(converted);
        var handler = NewHandler(
            NewCustomerLookup(),
            [new OrderWithQuotation(order, converted)],
            converted,
            NewQuotation("QUO-2026-0002", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var convertedRow = Assert.Single(
            page.Items, item => item.QuotationNumber == "QUO-2026-0001");
        Assert.Equal(order.Id.Value, convertedRow.OrderId);
        Assert.Equal("Pending", convertedRow.OrderStatus);

        // Sin pedido, la fila no trae nada que aprobar: OrderId y OrderStatus en null. El estado
        // Converted dice que ya se convirtio, pero no a que pedido ir ni si ya se aprobo.
        var openRow = Assert.Single(page.Items, item => item.QuotationNumber == "QUO-2026-0002");
        Assert.Null(openRow.OrderId);
        Assert.Null(openRow.OrderStatus);
    }

    // La busqueda del listado no trae las lineas a proposito, asi que "tiene productos" se
    // resuelve aparte y para toda la pagina de una vez.
    [Fact]
    public async Task ListSaysWhetherEachQuotationIsAlreadyADocument()
    {
        var complete = NewQuotation("QUO-2026-0001", ClientId, complete: true);
        var draft = NewQuotation("QUO-2026-0002", ClientId);
        var handler = NewHandler(NewCustomerLookup(), [], complete, draft);

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.True(
            Assert.Single(page.Items, item => item.QuotationNumber == "QUO-2026-0001")
                .IsComplete);
        Assert.False(
            Assert.Single(page.Items, item => item.QuotationNumber == "QUO-2026-0002")
                .IsComplete);
    }

    // Un borrador con productos pero sin vigencia no se puede enviar: el envio lo exige
    // (EnsureSendable) y la fila tiene que decirlo, o el menu ofrece algo que termina en 422.
    [Fact]
    public async Task ListReportsAnIncompleteQuotationEvenWhenItHasProducts()
    {
        var withoutValidity = NewQuotation("QUO-2026-0001", ClientId, withItem: true);
        var handler = NewHandler(NewCustomerLookup(), [], withoutValidity);

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(page.Items).IsComplete);
    }

    // Ver cotizaciones no es ver pedidos. Sin el permiso, la fila viaja sin pedido: es todo lo que
    // esa persona puede saber, y la pantalla no le ofrece ir a aprobar algo que tampoco podria.
    [Fact]
    public async Task ListHidesTheOrderFromSomeoneWhoCannotReadOrders()
    {
        var converted = NewQuotation("QUO-2026-0001", ClientId);
        var handler = new ListQuotationsHandler(
            new StubQuotationListRepository(converted),
            new StubOrderListRepository(new OrderWithQuotation(NewOrder(converted), converted)),
            NewCustomerLookup(),
            new StubQuotationAdvisorLookup(),
            new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.SaleRead));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).OrderId);
    }

    private static ListQuotationsQuery NewQuery() =>
        new(TenantId, null, null, null, null, null, null, null, Page: 1, PageSize: 10);

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    /// <summary><paramref name="complete"/> la deja como el documento que el backend acepta
    /// enviar y convertir: con una linea, vigencia y cuenta de cobro. <paramref name="withItem"/>
    /// agrega la linea sola, para el caso en que hay productos pero todavia falta el resto.
    /// </summary>
    private static Quotation NewQuotation(
        string number, Guid clientId, bool complete = false, bool withItem = false)
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            number,
            clientId,
            AdvisorId,
            validUntil: complete ? new DateOnly(2026, 12, 31) : null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: complete ? BillingAccount : null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

        if (complete || withItem)
        {
            quotation.AddItem(
                QuotationItemId.New(),
                Guid.CreateVersion7(),
                quantity: 1,
                unitPrice: 119_000m,
                discountPercentage: 0m,
                taxPercentage: 19,
                AdvisorId,
                Now);
        }

        return quotation;
    }

    private static Order NewOrder(Quotation quotation) =>
        Order.Create(
            OrderId.New(),
            TenantId,
            "VEN-2026-0001",
            quotation.Id,
            OrderPaymentStatus.PaymentPending,
            notes: null,
            AdvisorId,
            [],
            Now);

    private static ListQuotationsHandler NewHandler(
        StubQuotationCustomerLookup customers, params Quotation[] quotations) =>
        NewHandler(customers, [], quotations);

    private static ListQuotationsHandler NewHandler(
        StubQuotationCustomerLookup customers,
        OrderWithQuotation[] sales,
        params Quotation[] quotations) =>
        NewHandler(
            customers,
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            sales,
            quotations);

    private static ListQuotationsHandler NewHandler(
        StubQuotationCustomerLookup customers,
        StubQuotationAdvisorLookup advisors,
        params Quotation[] quotations) =>
        NewHandler(customers, advisors, [], quotations);

    private static ListQuotationsHandler NewHandler(
        StubQuotationCustomerLookup customers,
        StubQuotationAdvisorLookup advisors,
        OrderWithQuotation[] sales,
        Quotation[] quotations) =>
        new(new StubQuotationListRepository(quotations),
            new StubOrderListRepository(sales),
            customers,
            advisors,
            new StubExecutionContext(SubjectId, TenantId));
}
