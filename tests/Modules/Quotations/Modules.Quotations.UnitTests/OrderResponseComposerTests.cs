using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El detalle del pedido ya no trae la solicitud de la tienda (spec 2026-11-09): el checkout crea el pedido directo, así que un pedido
/// de la tienda se compone como cualquier otro.
/// </summary>
public sealed class OrderResponseComposerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ana Pérez",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    [Fact]
    public void TheDetailResponseHasNoStoreRequestProperty() =>
        Assert.Null(typeof(OrderDetailResponse).GetProperty("StoreRequest"));

    // Spec 2026-11-14: la nota del comprador viaja en el DTO y en la respuesta del detalle, aparte de las notas del asesor.
    [Fact]
    public void TheOrderDtoAndResponseCarryTheBuyerNotes()
    {
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003",
            new OrderHeader(ClientId, AdvisorId, "COP", null, IsRetail: false, null, false, []),
            QuotationParties.Empty, OrderBuilders.BillingAccount,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19)],
            OrderPaymentStatus.PaymentPending, "Shopify #1001", AdvisorId, [], Now, OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None, buyerNotes: "Sin cebolla");

        var dto = order.ToDto();

        Assert.Equal(("Sin cebolla", "Shopify #1001"), (dto.BuyerNotes, dto.Notes));
        Assert.Equal("Sin cebolla", OrderResponses.ToResponse(dto, documentYear: null).BuyerNotes);
    }

    // Spec retenciones por UVT §9: año igual → false; anterior → true; sin año (sin unidad o legada) → false.
    [Theory]
    [InlineData(2026, 2026, false)]
    [InlineData(2025, 2026, true)]
    [InlineData(null, 2026, false)]
    [InlineData(2025, null, false)]
    public void TaxUnitOutdatedComparesWithTheDocumentYear(int? taxUnitYear, int? documentYear, bool expected) =>
        Assert.Equal(expected, QuotationResponseComposer.IsOutdated(taxUnitYear, documentYear));

    // Review Focus 2: el 1 de enero a la 01:00 UTC es todavía 31 de diciembre en Bogotá; la UVT de ese año no está
    // desactualizada. El año sale del calendario del tenant, no de UTC.
    [Fact]
    public async Task TheDocumentYearIsTheTenantLocalYear()
    {
        var clock = new FixedTenantClock(Now);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            CreatedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            WithholdingBreakdown = [Verdict(taxUnitYear: 2025)],
        };

        var response = await Composer(clock: clock).ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(response.WithholdingBreakdown).TaxUnitOutdated);
        Assert.Equal([TenantId], clock.RequestedTenantIds);
    }

    // Sin ninguna fila con año no hay nada que comparar: el calendario no se lee.
    [Fact]
    public async Task WithoutTaxUnitYearsTheCalendarIsNotRead()
    {
        var clock = new FixedTenantClock(Now);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            WithholdingBreakdown = [Verdict(taxUnitYear: null)],
        };

        var response = await Composer(clock: clock).ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(response.WithholdingBreakdown).TaxUnitOutdated);
        Assert.Empty(clock.RequestedTenantIds);
    }

    private static WithholdingVerdictDto Verdict(int? taxUnitYear) => new(
        Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, 4m, false, null, false,
        "BelowMinimumBase", 100_000m, 4m, taxUnitYear is null ? null : "UVT", taxUnitYear is null ? null : 199_196m,
        taxUnitYear is null ? null : 49_799m, taxUnitYear, 0m);

    [Fact]
    public async Task AStoreOrderComposesLikeAnyOtherOrder()
    {
        // El pedido de la tienda ya es un Order completo: su detalle no le pide nada a Store.
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { Source = "Store" };

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Equal("Store", response.Order.Source);
        Assert.Equal("Asesora Uno", response.AdvisorName);
    }

    // Spec 2026-11-10 §5: el detalle trae los cuatro ejes, quién y cuándo de cada marca y el saldo, para que la
    // pantalla no los deduzca del historial ni reste comprobantes.
    [Fact]
    public async Task TheResponseCarriesTheAxesTheirMarksAndTheBalance()
    {
        var marker = new MemberId(Guid.CreateVersion7());
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now, ClientId, proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        order.MarkPaid(marker, requirePaymentProof: false, order.WithoutCosts(), Now.AddMinutes(1));
        order.Prepare(marker, Now.AddMinutes(2));
        order.Archive(marker, Now.AddMinutes(3));

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order.ToDto(), null, []), TestContext.Current.CancellationToken);

        var result = response.Order;
        Assert.Equal(
            ("Archived", "FullPaymentReceived", "Prepared", false),
            (result.Status, result.PaymentStatus, result.FulfillmentStatus, result.Invoiced));
        Assert.Equal((Now.AddMinutes(1), marker.Value), (result.PaidMarkedAt!.Value, result.PaidMarkedBy!.Value));
        Assert.Equal((Now.AddMinutes(2), marker.Value), (result.PreparedAt!.Value, result.PreparedBy!.Value));
        Assert.Equal((Now.AddMinutes(3), marker.Value), (result.ArchivedAt!.Value, result.ArchivedBy!.Value));
        Assert.Equal(19_000m, result.Balance);
    }

    [Fact]
    public void TheApprovalFieldsAreGone()
    {
        Assert.Null(typeof(OrderResponse).GetProperty("ApprovedAt"));
        Assert.Null(typeof(OrderResponse).GetProperty("ApprovedBy"));
        Assert.Null(typeof(OrderDto).GetProperty("ApprovedAt"));
        Assert.Null(typeof(OrderDto).GetProperty("ApprovedBy"));
    }

    [Fact]
    public async Task TheOrderDetailCarriesTheClientsContacts()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", "3115550101");
        var pedro = new QuotationClientContact(Guid.CreateVersion7(), "Pedro Ríos", "3125550102");

        var response = await ComposeAsync(laura, pedro);

        Assert.Equal(
            [new ClientContactDto(laura.Id, "Laura Gómez", "3115550101"), new ClientContactDto(pedro.Id, "Pedro Ríos", "3125550102")],
            response.ClientContacts);
    }

    [Fact]
    public async Task AClientWithoutContactsCarriesAnEmptyList()
    {
        var response = await ComposeAsync();

        Assert.NotNull(response.ClientContacts);
        Assert.Empty(response.ClientContacts);
    }

    [Fact]
    public async Task ContactsWithoutPhoneTravelWithPhoneNull()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", null);

        var response = await ComposeAsync(laura);

        Assert.Equal(new ClientContactDto(laura.Id, "Laura Gómez", null), Assert.Single(response.ClientContacts));
    }

    private static async Task<OrderDetailResponse> ComposeAsync(params QuotationClientContact[] contacts)
    {
        var customers = new StubQuotationCustomerLookup(Customer);
        customers.Contacts.AddRange(contacts);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto();
        return await Composer(customers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);
    }

    // Spec 2026-11-15 §4: el detalle trae la cuenta de comprador con su correo; una cuenta borrada sale null aunque quede
    // el id, y un pedido sin cuenta no consulta a Buyers.
    [Fact]
    public async Task TheDetailCarriesTheBuyerAccountWithItsEmail()
    {
        var accountId = Guid.CreateVersion7();
        var buyers = new StubQuotationBuyerAccountLookup(new QuotationBuyerAccountRef(accountId, "ana@example.com"));
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto()
            with { Source = "Store", BuyerAccountId = accountId };

        var response = await Composer(buyers: buyers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Equal(new BuyerAccountResponse(accountId, "ana@example.com"), response.BuyerAccount);
    }

    [Fact]
    public async Task ADeletedBuyerAccountComposesAsNoAccount()
    {
        var buyers = new StubQuotationBuyerAccountLookup();
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto()
            with { Source = "Store", BuyerAccountId = Guid.CreateVersion7() };

        var response = await Composer(buyers: buyers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Null(response.BuyerAccount);
        Assert.Equal(1, buyers.FindCalls);
    }

    [Fact]
    public async Task AnOrderWithoutBuyerAccountDoesNotAskBuyers()
    {
        var buyers = new StubQuotationBuyerAccountLookup();
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto();

        var response = await Composer(buyers: buyers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Null(response.BuyerAccount);
        Assert.Equal(0, buyers.FindCalls);
    }

    private static OrderResponseComposer Composer(
        StubQuotationCustomerLookup? customers = null,
        StubQuotationBuyerAccountLookup? buyers = null,
        FixedTenantClock? clock = null) =>
        new(
            customers ?? new StubQuotationCustomerLookup(Customer),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new StubQuotationWithholdingLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            buyers ?? new StubQuotationBuyerAccountLookup(),
            clock ?? new FixedTenantClock(Now));
}
