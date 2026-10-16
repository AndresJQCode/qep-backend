using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// SALE-01. El pedido no repite cliente, asesora ni totales —los lee de su cotización—, así que lo
/// que estas pruebas fijan es que la fila los traiga ya resueltos y que resolverlos cueste una
/// ida por página, no una por fila.
/// </summary>
public sealed class ListOrdersHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid OtherClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListPutsTheClientAndTheQuotationTotalsOnEachRow()
    {
        var customers = NewCustomerLookup();
        var handler = NewHandler(customers, NewRow("PED-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(page.Items);
        Assert.Equal("PED-2026-0001", row.OrderNumber);
        Assert.Equal("Ferretería El Tornillo", row.ClientName);
        Assert.Equal("Asesora Uno", row.AdvisorName);
        // Nace pendiente de revisión: quien convierte y quien aprueba son roles distintos.
        Assert.Equal("Pending", row.Status);
        Assert.Equal("COP", row.Currency);
    }

    // La fila de pedidos presenta a la asesora por su nombre, igual que la de cotizaciones (spec
    // 2026-09-11, D1, nota del 2026-09-15).
    [Fact]
    public async Task ListCarriesTheAdvisorNameWhenTheMemberHasOne()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId));

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
            NewRow("PED-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(page.Items).AdvisorName);
    }

    // La asesora es una referencia blanda, igual que el cliente: si la membresía no resuelve, la
    // fila viaja igual y sin etiqueta. Un pedido histórico tiene que poder leerse.
    [Fact]
    public async Task ListLeavesTheAdvisorNameNullWhenTheMemberDoesNotResolve()
    {
        var handler = NewHandler(
            NewCustomerLookup(),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", resolves: false),
            NewRow("PED-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).AdvisorName);
    }

    // La alternativa —una consulta por fila— es el N+1 que estos campos existen para evitar.
    [Fact]
    public async Task ListResolvesEveryClientNameInASingleLookup()
    {
        var customers = NewCustomerLookup();
        customers.Names[OtherClientId] = "Distribuidora del Sur";
        var handler = NewHandler(
            customers,
            NewRow("PED-2026-0001", ClientId),
            NewRow("PED-2026-0002", ClientId),
            NewRow("PED-2026-0003", OtherClientId));

        await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1, customers.FindNamesCalls);
        Assert.Equal(2, customers.LastRequestedIds.Count);
    }

    // El cliente es una referencia blanda entre módulos: si no resuelve, la fila viaja igual y
    // quien la muestra decide el respaldo. Un pedido histórico tiene que poder leerse.
    [Fact]
    public async Task ListLeavesTheClientNameNullWhenTheCustomerDoesNotResolve()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0002", OtherClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).ClientName);
    }

    // El CUC no vive en el pedido ni en la cotización: se resuelve a ids contra Customers y recién
    // entonces se filtra. Sin término no se consulta nada.
    [Fact]
    public async Task ListResolvesTheCucFilterToClientIdsBeforeSearching()
    {
        var customers = NewCustomerLookup();
        customers.IdsByCuc.Add(ClientId);
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", ClientId));
        var handler = NewHandler(customers, repository);

        await handler.HandleAsync(
            NewQuery() with { ClientCuc = "CUC-001" }, TestContext.Current.CancellationToken);

        Assert.Equal("CUC-001", customers.LastCucTerm);
        Assert.Equal([ClientId], repository.LastClientIds);
    }

    [Fact]
    public async Task ListDoesNotTouchCustomersWhenThereIsNoCucFilter()
    {
        var customers = NewCustomerLookup();
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", ClientId));
        var handler = NewHandler(customers, repository);

        await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(customers.LastCucTerm);
        Assert.Null(repository.LastClientIds);
    }

    // Texto libre por query string: un estado que no existe es un 422 con código de dominio, no
    // un filtro que en silencio no devuelve nada.
    [Theory]
    [InlineData("NotAStatus", "order.order.status_invalid")]
    public async Task ListRejectsAnUnknownStatus(string status, string code)
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery() with { Status = status }, TestContext.Current.CancellationToken));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task ListRejectsAnUnknownPaymentStatus()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery() with { PaymentStatus = "Whatever" },
                TestContext.Current.CancellationToken));

        Assert.Equal("order.order.payment_status_invalid", error.Code);
    }

    // Un pedido creado a la 01:00 del 15 en Madrid (23:00 UTC del 14) es del 15 para quien filtra
    // desde Madrid: el día se corta en la zona que manda el navegador, no en la del tenant.
    [Fact]
    public async Task ListCutsTheDayInTheTimeZoneOfWhoeverFilters()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", ClientId));
        var handler = NewHandler(NewCustomerLookup(), repository);
        var day = new DateOnly(2026, 10, 15);

        await handler.HandleAsync(
            NewQuery() with { ConvertedFrom = day, ConvertedTo = day, TimeZone = "Europe/Madrid" },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero)),
            repository.LastSearchRange);
    }

    [Fact]
    public async Task ListWithoutATimeZoneStillCutsTheDayOfTheTenant()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", ClientId));
        var handler = NewHandler(NewCustomerLookup(), repository);
        var day = new DateOnly(2026, 10, 15);

        await handler.HandleAsync(
            NewQuery() with { ConvertedFrom = day, ConvertedTo = day },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (new DateTimeOffset(2026, 10, 15, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 5, 0, 0, TimeSpan.Zero)),
            repository.LastSearchRange);
    }

    [Theory]
    [InlineData("Marte/Olympus")]
    [InlineData("SA Pacific Standard Time")]
    public async Task ListRejectsATimeZoneThatIsNotIana(string timeZone)
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery() with { ConvertedFrom = new DateOnly(2026, 10, 15), TimeZone = timeZone },
                TestContext.Current.CancellationToken));

        Assert.Equal("order.time_zone_invalid", error.Code);
    }

    // Spec 2026-10-15 §6.1: cliente, asesor y totales salen del pedido; un pedido manual no tiene
    // cotización y la fila lo dice con `Source` y los dos campos de cotización en null.
    [Fact]
    public async Task ListReadsClientAdvisorAndTotalsFromTheOrder()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("PED-2026-0001", ClientId, total: 23_800m));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(page.Items);
        Assert.Equal(("Ferretería El Tornillo", "Asesora Uno", "COP", 23_800m, "Manual", (Guid?)null, (string?)null),
            (row.ClientName, row.AdvisorName, row.Currency, row.Total, row.Source, row.QuotationId, row.QuotationNumber));
    }

    [Fact]
    public async Task ListRejectsAnUnknownSource()
    {
        var handler = NewHandler(NewCustomerLookup());

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewQuery() with { Source = "Telegram" }, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.source_invalid", error.Code);
    }

    [Fact]
    public async Task ListPassesTheParsedSourceToTheRepository()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", ClientId));
        var handler = NewHandler(NewCustomerLookup(), repository);

        await handler.HandleAsync(NewQuery() with { Source = "manual" }, TestContext.Current.CancellationToken);

        Assert.Equal(OrderSource.Manual, repository.LastSource);
    }

    private static ListOrdersQuery NewQuery() =>
        new(TenantId, null, null, null, null, null, null, null, null, Source: null, Page: 1, PageSize: 10);

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static ListOrdersHandler NewHandler(
        StubQuotationCustomerLookup customers, params Order[] rows) =>
        NewHandler(customers, new StubOrderListRepository(rows));

    private static ListOrdersHandler NewHandler(
        StubQuotationCustomerLookup customers,
        StubQuotationAdvisorLookup advisors,
        params Order[] rows) =>
        NewHandler(customers, new StubOrderListRepository(rows), advisors);

    private static ListOrdersHandler NewHandler(
        StubQuotationCustomerLookup customers,
        StubOrderListRepository repository,
        StubQuotationAdvisorLookup? advisors = null) =>
        new(
            repository,
            new StubQuotationRepository(AnyQuotation()),
            customers,
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));

    // Una cotización cualquiera: el doble de IQuotationRepository exige una, y los pedidos de estas
    // pruebas son manuales, así que nunca se consulta su número.
    private static Quotation AnyQuotation() =>
        Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null, defaultCurrency: "COP",
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);

    // Un pedido manual con una línea cuyo precio con IVA da el total pedido: 2 unidades a total/2.
    private static Order NewRow(string orderNumber, Guid clientId, decimal total = 23_800m) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            orderNumber,
            new OrderHeader(clientId, AdvisorId, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "7542",
                Currency = "COP",
            },
            [new OrderLineInput(Guid.CreateVersion7(), 2m, total / 2m, 0m, 19)],
            OrderPaymentStatus.PaymentPending,
            notes: null,
            AdvisorId,
            [],
            Now);
}
