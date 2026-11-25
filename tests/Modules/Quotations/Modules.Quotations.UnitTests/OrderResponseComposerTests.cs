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
            OrderPaymentStatus.PaymentPending, "Shopify #1001", AdvisorId, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None, buyerNotes: "Sin cebolla");

        var dto = order.ToDto();

        Assert.Equal(("Sin cebolla", "Shopify #1001"), (dto.BuyerNotes, dto.Notes));
        Assert.Equal("Sin cebolla", OrderResponses.ToResponse(dto, TaxUnitContext.None).BuyerNotes);
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

    // Spec alerta UVT §5.4: el año que tomaría la fila si hoy se refrescara, con la fecha del documento. Una sola consulta
    // por respuesta, con los países distintos de las filas en unidades.
    [Fact]
    public async Task LatestYearIsTheLoadedYearForTheCountry()
    {
        var withholdings = new StubQuotationWithholdingLookup().WithTaxUnit("CO", 60_000m, 2026);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            WithholdingBreakdown = [Verdict(taxUnitYear: 2025), Verdict(taxUnitYear: 2025)],
        };

        var response = await Composer(withholdings: withholdings)
            .ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.All(
            response.WithholdingBreakdown,
            entry => Assert.Equal(
                ((int?)2025, true, (int?)2026), (entry.TaxUnitYear, entry.TaxUnitOutdated, entry.TaxUnitLatestYear)));
        Assert.Equal(["CO"], Assert.Single(withholdings.TaxUnitRequests));
    }

    // Spec alerta UVT §8: un documento de 2026 cuando la última UVT cargada para el país es la de 2025. El último año es
    // el que Catalog da (2025), no el del documento; y taxUnitOutdated sigue mirando el año de cada fila contra el del
    // documento, así que la fila de 2025 queda desactualizada aunque no haya nada más nuevo que tomar.
    [Fact]
    public async Task LatestYearIsAnEarlierYearWhenTheDocumentYearIsNotLoaded()
    {
        var withholdings = new StubQuotationWithholdingLookup().WithTaxUnit("CO", 49_799m, 2025);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            WithholdingBreakdown = [Verdict(taxUnitYear: 2025), Verdict(taxUnitYear: 2024)],
        };

        var response = await Composer(withholdings: withholdings)
            .ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { ((int?)2025, true, (int?)2025), ((int?)2024, true, (int?)2025) },
            response.WithholdingBreakdown.Select(entry => (entry.TaxUnitYear, entry.TaxUnitOutdated, entry.TaxUnitLatestYear)));
    }

    // Sin ningún valor cargado para el país no hay año que ofrecer.
    [Fact]
    public async Task LatestYearIsNullWithoutAnyLoadedValue()
    {
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            WithholdingBreakdown = [Verdict(taxUnitYear: 2025)],
        };

        var response = await Composer().ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(response.WithholdingBreakdown).TaxUnitLatestYear);
    }

    // Una fila sin base en unidades no tiene UVT que refrescar, aunque su país tenga una cargada.
    [Fact]
    public void LatestYearIsNullForARowWithoutUnit()
    {
        var context = new TaxUnitContext(2026, new Dictionary<string, int> { ["CO"] = 2026 });

        var entry = Assert.Single(QuotationResponseComposer.ToBreakdownResponse([Verdict(taxUnitYear: null)], context));

        Assert.Null(entry.TaxUnitLatestYear);
    }

    // Lectura perezosa (spec alerta UVT §5.4): sin filas en unidades no se consulta Catalog ni el calendario.
    [Fact]
    public async Task NoUnitRowsDoNotQueryTheLookup()
    {
        var withholdings = new StubQuotationWithholdingLookup().WithTaxUnit("CO", 60_000m, 2026);
        var clock = new FixedTenantClock(Now);

        var context = await QuotationResponseComposer.TaxUnitContextAsync(
            clock, withholdings, TenantId, Now, [Verdict(taxUnitYear: null)], TestContext.Current.CancellationToken);

        Assert.Same(TaxUnitContext.None, context);
        Assert.Empty(withholdings.TaxUnitRequests);
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
            with
        { Source = "Store", BuyerAccountId = accountId };

        var response = await Composer(buyers: buyers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Equal(new BuyerAccountResponse(accountId, "ana@example.com"), response.BuyerAccount);
    }

    [Fact]
    public async Task ADeletedBuyerAccountComposesAsNoAccount()
    {
        var buyers = new StubQuotationBuyerAccountLookup();
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto()
            with
        { Source = "Store", BuyerAccountId = Guid.CreateVersion7() };

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

    // Spec 2026-11-16 F-8: un pedido manual no tiene cotización de origen, así que no hay instante que mostrar.
    [Fact]
    public async Task AManualOrderHasNoSourceQuotation()
    {
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { QuotationId = null, Source = "Manual" };

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Null(response.SourceQuotation);
    }

    // F-8: la cotización de origen viaja con su número y su CreatedAt; sin el instante (cotización no encontrada) no viaja.
    [Fact]
    public async Task TheSourceQuotationCarriesItsCreationInstant()
    {
        var quotationId = Guid.CreateVersion7();
        var createdAt = Now.AddDays(-3);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { QuotationId = quotationId };

        var withInstant = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order, "COT-2026-0001", [], createdAt), TestContext.Current.CancellationToken);
        var withoutInstant = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order, "COT-2026-0001", []), TestContext.Current.CancellationToken);

        Assert.Equal(new SourceQuotationResponse(quotationId, "COT-2026-0001", createdAt), withInstant.SourceQuotation);
        Assert.Null(withoutInstant.SourceQuotation);
    }

    // Spec 2026-11-16 §4: la fecha del pedido viaja en el DTO y en la respuesta.
    [Fact]
    public void TheOrderDtoAndResponseCarryTheOrderDate()
    {
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId);

        var dto = order.ToDto();

        Assert.Equal(order.OrderDate, dto.OrderDate);
        Assert.Equal(order.OrderDate, OrderResponses.ToResponse(dto, TaxUnitContext.None).OrderDate);
        Assert.Equal(order.OrderDate, order.ToListItemDto(null, null, null, requiresShipping: true).OrderDate);
    }

    // §3: el texto del historial, con fechas ISO sin depender de la cultura.
    [Fact]
    public void TheOrderDateChangeSummaryNamesBothDates() =>
        Assert.Equal(
            "Cambió la fecha del pedido de 2026-11-14 a 2026-11-10.",
            OrderChangeSummary.OrderDateChanged(new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 10)));

    // Mayorista fase 3 (D-7, adenda): el detalle nombra al distribuidor con el nombre de su tenant, dice quién aprobó y marca
    // las líneas que vinieron de la orden de compra.
    [Fact]
    public async Task AWholesaleDetailNamesTheDistributorAndMarksTheLines()
    {
        var order = WholesaleOrderFixtures.Wholesale(WholesaleOrderFixtures.Line(Guid.CreateVersion7(), 1m, 8_500m));
        var added = Guid.CreateVersion7();
        order.AddItem(OrderItemId.New(), added, added, 1m, 5_000m, 0m, 0, OrderLineCosts.None, WholesaleOrderFixtures.Advisor, Now);
        var approver = Guid.CreateVersion7();
        order.ApproveWholesale(new MemberId(approver), Now);
        var tenants = new StubTenantDirectory { DisplayName = "Distribuciones Norte" };

        var response = await Composer(tenants: tenants).ComposeAsync(
            TenantId, new OrderDetailDto(order.ToDto(), null, []), TestContext.Current.CancellationToken);

        var wholesale = response.Wholesale!;
        Assert.Equal((WholesaleOrderFixtures.BuyerTenantId, "Distribuciones Norte"), (wholesale.BuyerTenant.Id, wholesale.BuyerTenant.Name));
        Assert.Equal((WholesaleOrderFixtures.PurchaseOrderId, "OC-0007", "Approved"), (wholesale.PurchaseOrderId, wholesale.PurchaseOrderNumber, wholesale.Approval));
        Assert.Equal(approver, wholesale.ApprovedBy!.MemberId);
        Assert.False(wholesale.CancelledByBuyer);
        Assert.Equal([true, false], response.Items.Select(item => item.FromPurchaseOrder));
    }

    [Fact]
    public async Task AManualDetailHasNoWholesaleBlock()
    {
        var order = OrderBuilders.Pending(TenantId, new MemberId(Guid.CreateVersion7()), Now);

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order.ToDto(), null, []), TestContext.Current.CancellationToken);

        Assert.Null(response.Wholesale);
        Assert.All(response.Items, item => Assert.Null(item.FromPurchaseOrder));
    }

    [Fact]
    public async Task PartyResponseCarriesRegion()
    {
        var madrid = Guid.CreateVersion7();
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string>(),
            regions: new Dictionary<Guid, QuotationRegionRef> { [madrid] = new(madrid, "ES", "Madrid") });
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with
        {
            Parties =
            [
                new QuotationPartyDto(
                    Guid.CreateVersion7(), "Billing", "Bodega", null, null, null, null, null, null,
                    Country: "ES", CityName: "Madrid", RegionId: madrid),
            ],
        };

        var response = await Composer(geography: geography)
            .ComposeOrderAsync(TenantId, order, TestContext.Current.CancellationToken);

        Assert.Equal(new QuotationRegionDto(madrid, "Madrid"), Assert.Single(response.Parties).Region);
        Assert.Equal(1, geography.FindRegionsCalls);
    }

    private static OrderResponseComposer Composer(
        StubQuotationCustomerLookup? customers = null,
        StubQuotationBuyerAccountLookup? buyers = null,
        FixedTenantClock? clock = null,
        StubQuotationWithholdingLookup? withholdings = null,
        StubTenantDirectory? tenants = null,
        StubQuotationGeographyLookup? geography = null) =>
        new(
            customers ?? new StubQuotationCustomerLookup(Customer),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            withholdings ?? new StubQuotationWithholdingLookup(),
            geography ?? new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            buyers ?? new StubQuotationBuyerAccountLookup(),
            clock ?? new FixedTenantClock(Now),
            tenants ?? new StubTenantDirectory());
}
