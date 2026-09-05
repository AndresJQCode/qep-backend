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

    private static ListQuotationsQuery NewQuery() =>
        new(TenantId, null, null, null, null, null, null, null, Page: 1, PageSize: 10);

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static Quotation NewQuotation(string number, Guid clientId) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            number,
            clientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static ListQuotationsHandler NewHandler(
        StubQuotationCustomerLookup customers, params Quotation[] quotations) =>
        new(new StubQuotationListRepository(quotations),
            customers,
            new StubQuotationAdvisorLookup(),
            new StubExecutionContext(SubjectId, TenantId));
}
