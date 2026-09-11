using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// SALE-01. La venta no repite cliente, asesora ni totales —los lee de su cotización—, así que lo
/// que estas pruebas fijan es que la fila los traiga ya resueltos y que resolverlos cueste una
/// ida por página, no una por fila.
/// </summary>
public sealed class ListSalesHandlerTests
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
        var handler = NewHandler(customers, NewRow("VEN-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(page.Items);
        Assert.Equal("VEN-2026-0001", row.SaleNumber);
        Assert.Equal("Ferretería El Tornillo", row.ClientName);
        Assert.Equal("asesora@qcode.co", row.AdvisorEmail);
        // Nace pendiente de revisión: quien convierte y quien aprueba son roles distintos.
        Assert.Equal("Pending", row.Status);
        Assert.Equal("COP", row.Currency);
    }

    // La alternativa —una consulta por fila— es el N+1 que estos campos existen para evitar.
    [Fact]
    public async Task ListResolvesEveryClientNameInASingleLookup()
    {
        var customers = NewCustomerLookup();
        customers.Names[OtherClientId] = "Distribuidora del Sur";
        var handler = NewHandler(
            customers,
            NewRow("VEN-2026-0001", ClientId),
            NewRow("VEN-2026-0002", ClientId),
            NewRow("VEN-2026-0003", OtherClientId));

        await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1, customers.FindNamesCalls);
        Assert.Equal(2, customers.LastRequestedIds.Count);
    }

    // El cliente es una referencia blanda entre módulos: si no resuelve, la fila viaja igual y
    // quien la muestra decide el respaldo. Una venta histórica tiene que poder leerse.
    [Fact]
    public async Task ListLeavesTheClientNameNullWhenTheCustomerDoesNotResolve()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("VEN-2026-0002", OtherClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).ClientName);
    }

    // El CUC no vive en la venta ni en la cotización: se resuelve a ids contra Customers y recién
    // entonces se filtra. Sin término no se consulta nada.
    [Fact]
    public async Task ListResolvesTheCucFilterToClientIdsBeforeSearching()
    {
        var customers = NewCustomerLookup();
        customers.IdsByCuc.Add(ClientId);
        var repository = new StubSaleListRepository(NewRow("VEN-2026-0001", ClientId));
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
        var repository = new StubSaleListRepository(NewRow("VEN-2026-0001", ClientId));
        var handler = NewHandler(customers, repository);

        await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Null(customers.LastCucTerm);
        Assert.Null(repository.LastClientIds);
    }

    // Texto libre por query string: un estado que no existe es un 422 con código de dominio, no
    // un filtro que en silencio no devuelve nada.
    [Theory]
    [InlineData("NotAStatus", "sale.sale.status_invalid")]
    public async Task ListRejectsAnUnknownStatus(string status, string code)
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("VEN-2026-0001", ClientId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery() with { Status = status }, TestContext.Current.CancellationToken));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task ListRejectsAnUnknownPaymentStatus()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("VEN-2026-0001", ClientId));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery() with { PaymentStatus = "Whatever" },
                TestContext.Current.CancellationToken));

        Assert.Equal("sale.sale.payment_status_invalid", error.Code);
    }

    private static ListSalesQuery NewQuery() =>
        new(TenantId, null, null, null, null, null, null, null, null, Page: 1, PageSize: 10);

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static ListSalesHandler NewHandler(
        StubQuotationCustomerLookup customers, params SaleWithQuotation[] rows) =>
        NewHandler(customers, new StubSaleListRepository(rows));

    private static ListSalesHandler NewHandler(
        StubQuotationCustomerLookup customers, StubSaleListRepository repository) =>
        new(
            repository,
            customers,
            new StubQuotationAdvisorLookup("asesora@qcode.co"),
            new StubExecutionContext(SubjectId, TenantId));

    private static SaleWithQuotation NewRow(string saleNumber, Guid clientId)
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            clientId,
            AdvisorId,
            new DateOnly(2026, 10, 30),
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

        var sale = Sale.Create(
            SaleId.New(),
            TenantId,
            saleNumber,
            quotation.Id,
            SalePaymentStatus.PaymentPending,
            notes: null,
            AdvisorId,
            [],
            Now);

        return new SaleWithQuotation(sale, quotation);
    }
}
