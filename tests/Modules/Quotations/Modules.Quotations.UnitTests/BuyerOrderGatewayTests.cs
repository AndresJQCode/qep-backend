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

    private static QuotationParties Parties()
    {
        var billing = new QuotationPartyDetails
        {
            Name = "Ana Pérez",
            IdentificationNumber = "1020304050",
            IdentificationType = PartyIdentificationType.Cc,
            PersonType = PartyPersonType.Natural,
            Phone = "+573001112233",
            Email = "ana@example.com",
            Address = "Calle 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };
        var shipping = billing with { IdentificationNumber = null, IdentificationType = null, PersonType = null };
        return new QuotationParties(billing, shipping, BillingWithRetention: false, BillingVatSurplus: false);
    }

    private static Order StoreOrder(
        Guid? buyer,
        string number = "PED-2026-0001",
        DateTimeOffset? createdAt = null,
        Guid? tenantId = null,
        OrderId? id = null,
        bool paidMarked = false,
        OrderPaymentStatus payment = OrderPaymentStatus.PaymentPending)
    {
        var at = createdAt ?? Now.AddDays(-1);
        var order = Order.CreateFromStore(
            id ?? OrderId.New(), tenantId ?? TenantId, number, Header(), Parties(), Account,
            [Line("Aceite"), Line("Banano")], null, Advisor, at, $"store-checkout:{number}",
            buyerAccountId: buyer);
        if (payment != OrderPaymentStatus.PaymentPending)
        {
            order.SyncExternalPaymentStatus(payment, at);
        }

        if (paidMarked)
        {
            order.MarkPaid(Advisor, requirePaymentProof: false, at);
        }

        return order;
    }

    private BuyerOrderGateway Gateway(StubOrderListRepository orders, IQuotationsUnitOfWork? unitOfWork = null) =>
        new(orders, unitOfWork ?? new NoOpQuotationsUnitOfWork(), _audit, _lifecycle, new FixedClock(Now));

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
        Assert.Equal("Open", summary.Status);
        Assert.Equal("PaymentPending", summary.PaymentStatus);
        Assert.Equal(2, summary.LineCount);
        Assert.Equal("Aceite", summary.FirstLineName);
        Assert.True(summary.CanCancel);
    }

    [Theory]
    [InlineData(false, OrderPaymentStatus.PaymentPending, true)]
    [InlineData(false, OrderPaymentStatus.PartialPaymentReceived, false)]
    [InlineData(false, OrderPaymentStatus.FullPaymentReceived, false)]
    [InlineData(true, OrderPaymentStatus.PaymentPending, false)]
    public async Task ASummaryReportsCanCancelByRuleD5(bool paidMarked, OrderPaymentStatus payment, bool expected)
    {
        var orders = new StubOrderListRepository(StoreOrder(Buyer, paidMarked: paidMarked, payment: payment));

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
    public async Task CancelOfAPaidMarkedOrderIsAConflictAndWritesNothing()
    {
        var order = StoreOrder(Buyer, paidMarked: true);
        var orders = new StubOrderListRepository(order);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Gateway(orders).CancelAsync(TenantId, Buyer, order.Id.Value, CancellationToken.None));

        Assert.Equal("order.order.not_cancellable_by_buyer", error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
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
        var paidMeanwhile = StoreOrder(Buyer, id: id, paidMarked: true);
        var orders = new StubOrderListRepository(loaded);
        orders.QueueBuyerReads(loaded, paidMeanwhile);
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
