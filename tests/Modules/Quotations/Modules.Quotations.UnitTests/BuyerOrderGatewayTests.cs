using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Los pedidos del comprador de la tienda: listar, leer, anular y enlazar. La consulta SQL por comprador la
/// prueba integración; acá se prueba la forma de lo que sale y la anulación con su carrera.</summary>
public sealed class BuyerOrderGatewayTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid Buyer = Guid.CreateVersion7();
    private static readonly Guid Department = Guid.CreateVersion7();
    private static readonly Guid City = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingQuotationAuditPublisher _audit = new();
    private readonly RecordingQuotationLifecycleEventPublisher _lifecycle = new();

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(Guid.CreateVersion7(), Advisor, "COP", null, IsRetail: true, null, false, false);

    private static OrderLineInput Line(string name) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", name));

    private static readonly ShippingDetails Charged =
        new(null, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false);

    private static QuotationPartyDetails ColombianParty() => new()
    {
        Name = "Ana Pérez",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Cc,
        PersonType = PartyPersonType.Natural,
        Phone = "+573001112233",
        Email = "ana@example.com",
        Address = "Calle 1 # 2-3",
        DepartmentId = Department,
        CityId = City,
    };

    private static QuotationPartyDetails ShippingOf(QuotationPartyDetails billing) =>
        billing with { IdentificationNumber = null, IdentificationType = null, PersonType = null };

    private static QuotationParties Parties(bool vatSurplus = false, QuotationPartyDetails? billing = null)
    {
        billing ??= ColombianParty();
        return new QuotationParties(
            billing, ShippingOf(billing), BillingWithRetention: false, BillingVatSurplus: vatSurplus);
    }

    private static Order StoreOrder(
        Guid? buyer,
        string number = "PED-2026-0001",
        DateTimeOffset? createdAt = null,
        Guid? tenantId = null,
        OrderId? id = null,
        OrderStatus status = OrderStatus.Pending,
        OrderPaymentStatus payment = OrderPaymentStatus.PaymentPending,
        string? notes = null,
        QuotationParties? parties = null)
    {
        var at = createdAt ?? Now.AddDays(-1);
        var order = Order.CreateFromStore(
            id ?? OrderId.New(), tenantId ?? TenantId, number, Header(), parties ?? Parties(), Account,
            [Line("Aceite"), Line("Banano")], notes, Advisor, at, $"store-checkout:{number}",
            buyerAccountId: buyer);
        if (payment != OrderPaymentStatus.PaymentPending)
        {
            order.SyncExternalPaymentStatus(payment, at);
        }

        if (status == OrderStatus.Approved)
        {
            order.Approve(Advisor, at);
        }

        return order;
    }

    private BuyerOrderGateway Gateway(
        StubOrderListRepository orders,
        IQuotationsUnitOfWork? unitOfWork = null,
        IQuotationGeographyLookup? geography = null) =>
        new(
            orders, unitOfWork ?? new NoOpQuotationsUnitOfWork(), _audit, _lifecycle, new FixedClock(Now),
            geography ?? new StubQuotationGeographyLookup(
                new Dictionary<Guid, string> { [City] = "Cali" },
                departmentNames: new Dictionary<Guid, string> { [Department] = "Valle del Cauca" }));

    /// <summary>Lo que hace el ChangeTracker real al descartar: el historial, la auditoría y el evento viven en el
    /// mismo DbContext que el pedido y se van con él.</summary>
    private ConcurrencyFailingUnitOfWork FailingUnitOfWork(StubOrderListRepository orders, int failures) =>
        new(failures, () =>
        {
            orders.History.Clear();
            _audit.Actions.Clear();
            _lifecycle.Published.Clear();
        });

    [Fact]
    public async Task ListReturnsOnlyTheBuyerOrdersOfTheTenantNewestFirst()
    {
        var orders = new StubOrderListRepository(
            StoreOrder(Buyer, "PED-0001", Now.AddDays(-3)),
            StoreOrder(Buyer, "PED-0003", Now.AddDays(-1)),
            StoreOrder(Buyer, "PED-0002", Now.AddDays(-2)),
            StoreOrder(Guid.CreateVersion7(), "PED-0004", Now),
            StoreOrder(Buyer, "PED-0005", Now, tenantId: Guid.CreateVersion7()));

        var page = await Gateway(orders).ListAsync(TenantId, Buyer, 1, 20, CancellationToken.None);

        Assert.Equal(["PED-0003", "PED-0002", "PED-0001"], page.Items.Select(item => item.Number));
        Assert.Equal((1, 20, 3), (page.Page, page.PageSize, page.Total));
    }

    [Fact]
    public async Task ASummaryCarriesLineCountFirstLineNameEnumNamesAndCanCancel()
    {
        var order = StoreOrder(Buyer);
        var orders = new StubOrderListRepository(order);

        var summary = Assert.Single((await Gateway(orders).ListAsync(TenantId, Buyer, 1, 20, CancellationToken.None)).Items);

        Assert.Equal(order.Id.Value, summary.Id);
        Assert.Equal(order.CreatedAt, summary.CreatedAt);
        Assert.Equal((order.Total, "COP"), (summary.Total, summary.Currency));
        Assert.Equal("Pending", summary.Status);
        Assert.Equal("PaymentPending", summary.PaymentStatus);
        Assert.Equal(2, summary.LineCount);
        Assert.Equal("Aceite", summary.FirstLineName);
        Assert.True(summary.CanCancel);
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderPaymentStatus.PaymentPending, true)]
    [InlineData(OrderStatus.Pending, OrderPaymentStatus.PartialPaymentReceived, false)]
    [InlineData(OrderStatus.Pending, OrderPaymentStatus.FullPaymentReceived, false)]
    [InlineData(OrderStatus.Approved, OrderPaymentStatus.PaymentPending, false)]
    public async Task ASummaryReportsCanCancelByRuleD5(OrderStatus status, OrderPaymentStatus payment, bool expected)
    {
        var orders = new StubOrderListRepository(StoreOrder(Buyer, status: status, payment: payment));

        var summary = Assert.Single((await Gateway(orders).ListAsync(TenantId, Buyer, 1, 20, CancellationToken.None)).Items);

        Assert.Equal(expected, summary.CanCancel);
    }

    [Fact]
    public async Task FindReturnsNullForAnotherBuyersOrder()
    {
        var order = StoreOrder(Guid.CreateVersion7());
        var orders = new StubOrderListRepository(order);

        Assert.Null(await Gateway(orders).FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None));
    }

    [Fact]
    public async Task FindComputesLineTotalAndCanCancel()
    {
        var order = StoreOrder(Buyer);
        var orders = new StubOrderListRepository(order);

        var detail = await Gateway(orders).FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);

        Assert.NotNull(detail);
        var first = order.Items.OrderBy(item => item.Position).First();
        var line = detail.Lines[0];
        Assert.Equal(("Aceite", first.Quantity, first.UnitPrice), (line.Name, line.Quantity, line.UnitPrice));
        Assert.Equal(first.Subtotal + first.TaxAmount, line.LineTotal);
        Assert.True(first.TaxAmount > 0m);
        Assert.Equal(["Aceite", "Banano"], detail.Lines.Select(l => l.Name));
        Assert.Equal((order.OrderNumber, 2, "Aceite"), (detail.Number, detail.LineCount, detail.FirstLineName));
        Assert.Null(detail.CancelledAt);
        Assert.True(detail.CanCancel);
    }

    private static Order StoreOrderWithProofStillPaymentPending()
    {
        var order = StoreOrder(Buyer);
        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 5_000m)],
            OrderPaymentStatus.PaymentPending,
            null,
            Advisor,
            Now.AddHours(-1));
        return order;
    }

    [Fact]
    public async Task ASummaryAndTheDetailReportCanCancelFalseWhenAProofIsRegisteredEvenIfStillPaymentPending()
    {
        var order = StoreOrderWithProofStillPaymentPending();
        var orders = new StubOrderListRepository(order);

        var summary = Assert.Single((await Gateway(orders).ListAsync(TenantId, Buyer, 1, 20, CancellationToken.None)).Items);
        var detail = await Gateway(orders).FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);

        Assert.Equal("PaymentPending", summary.PaymentStatus);
        Assert.False(summary.CanCancel);
        Assert.NotNull(detail);
        Assert.False(detail.CanCancel);
    }

    [Fact]
    public async Task FindReportsCanCancelFalseWhenAPaymentIsRegistered()
    {
        var order = StoreOrder(Buyer, payment: OrderPaymentStatus.PartialPaymentReceived);
        var orders = new StubOrderListRepository(order);

        var detail = await Gateway(orders).FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("PartialPaymentReceived", detail.PaymentStatus);
        Assert.False(detail.CanCancel);
    }

    [Fact]
    public async Task CancelCancelsWithoutAMemberAuditsByIntegrationAndPublishesTheEvent()
    {
        var order = StoreOrder(Buyer);
        var orders = new StubOrderListRepository(order);

        var detail = await Gateway(orders).CancelAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Cancelled", detail.Status);
        Assert.Equal(Now, detail.CancelledAt);
        Assert.False(detail.CanCancel);
        var entry = Assert.Single(orders.History);
        Assert.Equal(OrderHistoryEventType.Cancelled, entry.EventType);
        Assert.Null(entry.MemberId);
        Assert.Equal("Anulado: Cancelado por el comprador desde la tienda", entry.Details);
        Assert.Equal(["quotation.order.cancelled"], _audit.Actions);
        Assert.Equal("cancelled", Assert.Single(_lifecycle.Published).Event);
    }

    [Fact]
    public async Task CancelOfAnUnknownOrderReturnsNull()
    {
        var orders = new StubOrderListRepository(StoreOrder(Guid.CreateVersion7()));

        Assert.Null(await Gateway(orders).CancelAsync(TenantId, Buyer, Guid.CreateVersion7(), CancellationToken.None));
        Assert.Empty(orders.History);
    }

    [Fact]
    public async Task CancelOfAnApprovedOrderIsAConflictAndWritesNothing()
    {
        var order = StoreOrder(Buyer, status: OrderStatus.Approved);
        var orders = new StubOrderListRepository(order);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Gateway(orders).CancelAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None));

        Assert.Equal("order.order.not_cancellable_by_buyer", error.Code);
        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Empty(orders.History);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_lifecycle.Published);
    }

    [Fact]
    public async Task CancelOfAPendingOrderWithPaymentIsAConflict()
    {
        var order = StoreOrder(Buyer, payment: OrderPaymentStatus.FullPaymentReceived);
        var orders = new StubOrderListRepository(order);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Gateway(orders).CancelAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None));

        Assert.Equal("order.order.not_cancellable_by_buyer", error.Code);
        Assert.Empty(orders.History);
    }

    [Fact]
    public async Task ACancelThatLosesTheRaceRereadsAndAnswersConflict()
    {
        var id = OrderId.New();
        var loaded = StoreOrder(Buyer, id: id);
        var approvedMeanwhile = StoreOrder(Buyer, id: id, status: OrderStatus.Approved);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueBuyerReads(loaded, approvedMeanwhile);
        var unitOfWork = FailingUnitOfWork(orders, failures: 1);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Gateway(orders, unitOfWork).CancelAsync(TenantId, Buyer, id.Value, CancellationToken.None));

        Assert.Equal("order.order.not_cancellable_by_buyer", error.Code);
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.Empty(orders.History);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_lifecycle.Published);
    }

    [Fact]
    public async Task ACancelThatLosesTheRaceToAHarmlessEditRetriesOnce()
    {
        var id = OrderId.New();
        var loaded = StoreOrder(Buyer, id: id);
        var editedMeanwhile = StoreOrder(Buyer, id: id);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueBuyerReads(loaded, editedMeanwhile);
        var unitOfWork = FailingUnitOfWork(orders, failures: 1);

        var detail = await Gateway(orders, unitOfWork).CancelAsync(TenantId, Buyer, id.Value, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Cancelled", detail.Status);
        Assert.Equal(OrderStatus.Cancelled, editedMeanwhile.Status);
        Assert.Equal(2, unitOfWork.SaveCalls);
        Assert.Single(orders.History);
        Assert.Equal(["quotation.order.cancelled"], _audit.Actions);
        Assert.Single(_lifecycle.Published);
    }

    [Fact]
    public async Task ACancelThatLosesTheRaceTwiceIsAConflictNotAPreconditionFailure()
    {
        var id = OrderId.New();
        var loaded = StoreOrder(Buyer, id: id);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueBuyerReads(loaded, StoreOrder(Buyer, id: id));
        var unitOfWork = FailingUnitOfWork(orders, failures: 2);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Gateway(orders, unitOfWork).CancelAsync(TenantId, Buyer, id.Value, CancellationToken.None));

        Assert.Equal("order.order.not_cancellable_by_buyer", error.Code);
        Assert.Equal(2, unitOfWork.SaveCalls);
        Assert.Empty(orders.History);
        Assert.Empty(_lifecycle.Published);
    }

    [Fact]
    public async Task ACancelWhoseOrderVanishesDuringTheRaceReturnsNull()
    {
        var loaded = StoreOrder(Buyer);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueBuyerReads(loaded, StoreOrder(Guid.CreateVersion7(), id: loaded.Id));
        var unitOfWork = FailingUnitOfWork(orders, failures: 1);

        Assert.Null(await Gateway(orders, unitOfWork).CancelAsync(TenantId, Buyer, loaded.Id.Value, CancellationToken.None));
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.Empty(orders.History);
    }

    [Fact]
    public async Task LinkSetsTheBuyerOnlyWhenEmpty()
    {
        var unlinked = StoreOrder(null, "PED-0001");
        var other = Guid.CreateVersion7();
        var linked = StoreOrder(other, "PED-0002");
        var orders = new StubOrderListRepository(unlinked, linked);
        var gateway = Gateway(orders);

        Assert.True(await gateway.LinkAsync(TenantId, unlinked.Id.Value, Buyer, CancellationToken.None));
        Assert.Equal(Buyer, unlinked.BuyerAccountId);

        Assert.False(await gateway.LinkAsync(TenantId, linked.Id.Value, Buyer, CancellationToken.None));
        Assert.Equal(other, linked.BuyerAccountId);

        Assert.False(await gateway.LinkAsync(TenantId, Guid.CreateVersion7(), Buyer, CancellationToken.None));
    }

    // El enlace no sube la versión, pero la versión es token de concurrencia: si otra escritura la subió entre la
    // lectura y el guardado, se relee y se intenta una vez más, como en la anulación.
    [Fact]
    public async Task ALinkThatLosesTheRaceToAnEditRereadsAndLinksOnce()
    {
        var id = OrderId.New();
        var loaded = StoreOrder(null, id: id);
        var editedMeanwhile = StoreOrder(null, id: id);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueIdReads(loaded, editedMeanwhile);
        var unitOfWork = FailingUnitOfWork(orders, failures: 1);

        Assert.True(await Gateway(orders, unitOfWork).LinkAsync(TenantId, id.Value, Buyer, CancellationToken.None));

        Assert.Equal(Buyer, editedMeanwhile.BuyerAccountId);
        Assert.Equal(2, unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task ALinkThatLosesTheRaceToAnotherBuyerDoesNotOverwriteIt()
    {
        var id = OrderId.New();
        var other = Guid.CreateVersion7();
        var loaded = StoreOrder(null, id: id);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueIdReads(loaded, StoreOrder(other, id: id));
        var unitOfWork = FailingUnitOfWork(orders, failures: 1);

        Assert.False(await Gateway(orders, unitOfWork).LinkAsync(TenantId, id.Value, Buyer, CancellationToken.None));

        Assert.Equal(1, unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task ALinkThatLosesTheRaceTwiceLetsTheConcurrencyErrorThrough()
    {
        var id = OrderId.New();
        var loaded = StoreOrder(null, id: id);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueIdReads(loaded, StoreOrder(null, id: id));
        var unitOfWork = FailingUnitOfWork(orders, failures: 2);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            Gateway(orders, unitOfWork).LinkAsync(TenantId, id.Value, Buyer, CancellationToken.None));

        Assert.Equal(2, unitOfWork.SaveCalls);
    }

    private static async Task<BuyerOrderDetail> DetailOf(BuyerOrderGateway gateway, Order order)
    {
        var detail = await gateway.FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);
        Assert.NotNull(detail);
        return detail;
    }

    [Fact]
    public async Task AFreshStoreOrderCarriesItsTotalsWithoutFreightOrCarrier()
    {
        var order = StoreOrder(Buyer);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal((order.Subtotal, order.TaxAmount), (detail.Subtotal, detail.TaxAmount));
        Assert.Equal(0m, detail.FreightTotal);
        Assert.Equal(detail.Total, detail.Subtotal + detail.TaxAmount + detail.FreightTotal);
        Assert.False(detail.IsStorePickup);
        Assert.Null(detail.CarrierName);
        Assert.Equal("BillingParty", detail.BilledTo);
    }

    [Fact]
    public async Task AChargedFreightAddsToTheIdentityAndNamesTheCarrier()
    {
        var order = StoreOrder(Buyer);
        order.SetShipping(Charged, Advisor, Now);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal(order.FreightSubtotal + order.FreightTaxAmount, detail.FreightTotal);
        Assert.True(detail.FreightTotal > 0m);
        Assert.Equal(detail.Total, detail.Subtotal + detail.TaxAmount + detail.FreightTotal);
        Assert.Equal("Coordinadora", detail.CarrierName);
    }

    [Fact]
    public async Task WithVatSurplusTheLinesGoWithoutTaxAndAddUpToTheTotal()
    {
        var order = StoreOrder(Buyer, parties: Parties(vatSurplus: true));
        order.SetShipping(Charged, Advisor, Now);
        Assert.True(order.AppliesVatSurplus);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal(0m, detail.TaxAmount);
        var items = order.Items.OrderBy(item => item.Position).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            Assert.Equal(items[i].UnitPriceWithoutTax, detail.Lines[i].UnitPrice);
            Assert.Equal(items[i].Subtotal, detail.Lines[i].LineTotal);
        }

        Assert.Equal(
            detail.Total,
            Math.Round(detail.Lines.Sum(line => line.LineTotal), 2, MidpointRounding.AwayFromZero) + detail.FreightTotal);
        Assert.Equal(detail.Total, detail.Subtotal + detail.TaxAmount + detail.FreightTotal);
    }

    [Fact]
    public async Task CancelReturnsTheSameExtendedDetail()
    {
        var order = StoreOrder(Buyer, notes: "Entregar en portería", parties: Parties(vatSurplus: true));
        order.UpdateNotes("Texto del asesor", Now);
        var orders = new StubOrderListRepository(order);

        var detail = await Gateway(orders).CancelAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Cancelled", detail.Status);
        Assert.Equal("Entregar en portería", detail.BuyerNotes);
        Assert.NotNull(detail.Billing);
        Assert.Equal(order.Items.OrderBy(item => item.Position).First().Subtotal, detail.Lines[0].LineTotal);
    }

    [Theory]
    [InlineData(PartyIdentificationType.Cc, null, "CC", "CO")]
    [InlineData(PartyIdentificationType.Nit, null, "NIT", "CO")]
    [InlineData(PartyIdentificationType.TaxId, "US", "TAX_ID", "US")]
    public async Task TheBillingCarriesTheWireDocumentTypeAndCountry(
        PartyIdentificationType type, string? country, string expectedType, string expectedCountry)
    {
        var party = country is null
            ? ColombianParty() with { IdentificationType = type }
            : ColombianParty() with
            {
                IdentificationType = type, Country = country, DepartmentId = null, CityId = null, CityName = "Miami"
            };
        var order = StoreOrder(Buyer, parties: Parties(billing: party));

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        var billing = detail.Billing;
        Assert.NotNull(billing);
        Assert.Equal(
            ("Ana Pérez", expectedType, "1020304050", "ana@example.com", "+573001112233", "Calle 1 # 2-3", expectedCountry),
            (billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Email, billing.Phone,
                billing.Address, billing.Country));
        if (country is not null)
        {
            Assert.Equal((null, "Miami"), (billing.DepartmentName, billing.CityName));
        }
    }

    [Fact]
    public async Task ColombianPlacesComeFromTheLookupOneCallPerDictionary()
    {
        var order = StoreOrder(Buyer);
        var geography = new StubQuotationGeographyLookup(
            new Dictionary<Guid, string> { [City] = "Cali" },
            departmentNames: new Dictionary<Guid, string> { [Department] = "Valle del Cauca" });

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order), geography: geography), order);

        Assert.NotNull(detail.Billing);
        Assert.NotNull(detail.Shipping);
        Assert.Equal(("Valle del Cauca", "Cali"), (detail.Billing.DepartmentName, detail.Billing.CityName));
        Assert.Equal(("Valle del Cauca", "Cali"), (detail.Shipping.DepartmentName, detail.Shipping.CityName));
        Assert.Equal(
            ("Ana Pérez", "+573001112233", "Calle 1 # 2-3", "CO"),
            (detail.Shipping.Name, detail.Shipping.Phone, detail.Shipping.Address, detail.Shipping.Country));
        Assert.Equal((1, 1), (geography.FindDepartmentNamesCalls, geography.FindCityNamesCalls));
        Assert.Equal([Department], geography.LastDepartmentRequestedIds);
        Assert.Equal([City], geography.LastCityRequestedIds);
    }

    [Fact]
    public async Task AnIdTheLookupDoesNotKnowIsNull()
    {
        var order = StoreOrder(Buyer);
        var geography = new StubQuotationGeographyLookup(new Dictionary<Guid, string>());

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order), geography: geography), order);

        Assert.NotNull(detail.Billing);
        Assert.Equal((null, null, "CO"), (detail.Billing.DepartmentName, detail.Billing.CityName, detail.Billing.Country));
    }

    [Fact]
    public async Task AForeignPartyTakesItsCityAsTextAndSkipsTheLookup()
    {
        var foreign = ColombianParty() with
        {
            Country = "ES", IdentificationType = PartyIdentificationType.Nif, DepartmentId = null, CityId = null,
            CityName = "Madrid"
        };
        var order = StoreOrder(Buyer, parties: Parties(billing: foreign));
        var geography = new StubQuotationGeographyLookup(new Dictionary<Guid, string>());

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order), geography: geography), order);

        Assert.NotNull(detail.Billing);
        Assert.NotNull(detail.Shipping);
        Assert.Equal(("ES", null, "Madrid"), (detail.Billing.Country, detail.Billing.DepartmentName, detail.Billing.CityName));
        Assert.Equal(("ES", null, "Madrid"), (detail.Shipping.Country, detail.Shipping.DepartmentName, detail.Shipping.CityName));
        Assert.Equal((0, 0), (geography.FindDepartmentNamesCalls, geography.FindCityNamesCalls));
    }

    [Fact]
    public async Task BillingToTheFinalConsumerHidesTheBillingParty()
    {
        var order = StoreOrder(Buyer);
        order.SetBilling(null, true, false, null, null, Advisor, Now);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal("FinalConsumer", detail.BilledTo);
        Assert.Null(detail.Billing);
        Assert.NotNull(detail.Shipping);
    }

    [Fact]
    public async Task BillingToTheCustomerHidesTheBillingParty()
    {
        var order = StoreOrder(Buyer);
        order.SetBilling(null, false, false, null, null, Advisor, Now);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal("Customer", detail.BilledTo);
        Assert.Null(detail.Billing);
    }

    [Fact]
    public async Task AStorePickupHasNoShipping()
    {
        var billing = ColombianParty();
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0009", Header(),
            new QuotationParties(
                billing, ShippingOf(billing), IsStorePickup: true, BillingWithRetention: false, BillingVatSurplus: false),
            Account, ShippingDetails.None, [Line("Aceite")], OrderPaymentStatus.PaymentPending, null, Advisor, [], Now);
        order.LinkBuyerAccount(Buyer);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.True(detail.IsStorePickup);
        Assert.Null(detail.Shipping);
        Assert.NotNull(detail.Billing);
    }

    [Fact]
    public async Task BuyerNotesComeFromTheCheckoutNotFromTheAdvisor()
    {
        var order = StoreOrder(Buyer, notes: "Entregar en portería");
        order.UpdateNotes("Texto del asesor", Now);

        var detail = await DetailOf(Gateway(new StubOrderListRepository(order)), order);

        Assert.Equal("Entregar en portería", detail.BuyerNotes);
    }

    [Fact]
    public async Task AGeographyFailureIsNotDisguisedAsAMissingName()
    {
        var order = StoreOrder(Buyer);
        var gateway = Gateway(new StubOrderListRepository(order), geography: new ThrowingGeographyLookup());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.FindAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None));

        Assert.Equal("geography down", error.Message);
    }

    private sealed class ThrowingGeographyLookup : IQuotationGeographyLookup
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindCityNamesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("geography down");

        public Task<IReadOnlyDictionary<Guid, string>> FindCoordinadoraCityNamesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("geography down");

        public Task<IReadOnlyDictionary<Guid, string>> FindDepartmentNamesAsync(
            IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("geography down");
    }

    /// <summary>Lanza la excepción de concurrencia en los primeros <paramref name="failures"/> guardados y avisa cada
    /// descarte, para que la prueba limpie lo que el ChangeTracker real habría soltado.</summary>
    private sealed class ConcurrencyFailingUnitOfWork(int failures, Action onDiscard) : IQuotationsUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (SaveCalls <= failures)
            {
                throw new RequestConcurrencyException("concurrency.conflict", "The order changed.");
            }

            return Task.FromResult(1);
        }

        public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IQuotationsTransaction>(new NoOpQuotationsTransaction());

        public void DiscardPendingChanges() => onDiscard();
    }
}
