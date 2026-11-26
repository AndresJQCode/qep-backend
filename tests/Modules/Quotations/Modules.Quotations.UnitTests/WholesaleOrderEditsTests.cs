using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.WholesaleOrderFixtures;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// D-4 y D-5: la edición de un pedido mayorista. Sin escalas: la línea nueva toma el precio de lista del distribuidor (o el
/// base del catálogo, si no hay lista), cambiar la cantidad no reprecia y el precio explícito manda. Fuera de un pedido
/// mayorista, mandar un precio es 422.
/// </summary>
public sealed class WholesaleOrderEditsTests
{
    private static readonly Guid Kept = Guid.CreateVersion7();
    private static readonly Guid Added = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly StubQuotationProductPricingLookup Catalog =
        new(Variant(Kept, 20_000m), Variant(Added, 10_000m));

    private static Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order, StubWholesaleLinePricing pricing, params OrderItemAddition[] desired) =>
        OrderItemEdits.ApplyAsync(
            order, desired, Catalog, new RecordingOrderVariantCostLookup(), pricing, TenantId, Advisor, Now.AddHours(1),
            isRetail: false, Ct);

    [Fact]
    public async Task AnAddedLineTakesTheDistributorListPrice()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));
        var pricing = new StubWholesaleLinePricing(new Dictionary<Guid, decimal> { [Added] = 8_500m });

        await ApplyAsync(order, pricing, new OrderItemAddition(Kept, 2m), new OrderItemAddition(Added, 6m));

        var line = order.Items.Single(item => item.VariantId == Added);
        Assert.Equal((8_500m, 0m, (Guid?)null), (line.UnitPrice, line.DiscountPercentage, line.SourcePurchaseOrderLineId));
        var call = Assert.Single(pricing.Calls);
        Assert.Equal((TenantId, BuyerTenantId, "COP"), (call.TenantId, call.BuyerTenantId, call.Currency));
        Assert.Equal([Added], call.VariantIds);
    }

    [Fact]
    public async Task AnAddedLineWithoutListPriceTakesTheBasePrice()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));

        var edits = await ApplyAsync(
            order, new StubWholesaleLinePricing(), new OrderItemAddition(Kept, 2m), new OrderItemAddition(Added, 6m));

        // 6 u. caen en la escala del 10 %: en un pedido mayorista no aplica.
        var line = order.Items.Single(item => item.VariantId == Added);
        Assert.Equal((10_000m, 0m), (line.UnitPrice, line.DiscountPercentage));
        Assert.Equal(OrderItemEditKind.Added, Assert.Single(edits).Kind);
    }

    [Fact]
    public async Task AQuantityChangeKeepsTheUnitPrice()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));

        var edits = await ApplyAsync(order, new StubWholesaleLinePricing(), new OrderItemAddition(Kept, 6m));

        var line = Assert.Single(order.Items);
        Assert.Equal((6m, 11_900m, 0m), (line.Quantity, line.UnitPrice, line.DiscountPercentage));
        Assert.Equal(OrderItemEditKind.QuantityChanged, Assert.Single(edits).Kind);
    }

    [Fact]
    public async Task AnExplicitUnitPriceOverridesIt()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));
        var pricing = new StubWholesaleLinePricing(new Dictionary<Guid, decimal> { [Added] = 8_500m });

        var edits = await ApplyAsync(
            order, pricing, new OrderItemAddition(Kept, 2m, 9_000m), new OrderItemAddition(Added, 1m, 7_000m));

        Assert.Equal(9_000m, order.Items.Single(item => item.VariantId == Kept).UnitPrice);
        Assert.Equal(7_000m, order.Items.Single(item => item.VariantId == Added).UnitPrice);
        Assert.Equal(
            [OrderItemEditKind.Added, OrderItemEditKind.UnitPriceChanged], edits.Select(edit => edit.Kind).Order());
        var priceEdit = edits.Single(edit => edit.Kind == OrderItemEditKind.UnitPriceChanged);
        Assert.Equal((11_900m, 9_000m), (priceEdit.PreviousUnitPrice, priceEdit.UnitPrice));
    }

    [Fact]
    public async Task TheSameUnitPriceIsNotAnEdit()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));

        var edits = await ApplyAsync(order, new StubWholesaleLinePricing(), new OrderItemAddition(Kept, 2m, 11_900m));

        Assert.Empty(edits);
    }

    [Fact]
    public async Task AUnitPriceOnAManualOrderIs422()
    {
        var order = Manual(new OrderLineInput(Kept, Kept, 2m, 20_000m, 0m, 0));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(order, new StubWholesaleLinePricing(), new OrderItemAddition(Kept, 2m, 9_000m)));

        Assert.Equal(WholesaleOrderErrors.UnitPriceNotEditable, error.Code);
        Assert.Equal(20_000m, Assert.Single(order.Items).UnitPrice);
    }

    [Fact]
    public async Task AnApprovedWholesaleOrderCannotBeEdited()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));
        order.ApproveWholesale(Advisor, Now);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(order, new StubWholesaleLinePricing(), new OrderItemAddition(Kept, 3m)));

        Assert.False(order.IsEditable);
        Assert.Equal(ExternalOrderErrors.NotEditable, error.Code);
    }

    [Fact]
    public async Task CancellingAWholesaleOrderPublishesWholesaleCancelled()
    {
        var order = Wholesale(Line(Kept, 2m, 11_900m));
        var events = new RecordingQuotationLifecycleEventPublisher();
        var handler = new CancelOrderHandler(
            new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), new RecordingQuotationAuditPublisher(), events,
            new StubMembershipDirectory(Guid.CreateVersion7()), new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now),
            new NeverOpenExternalInvoice());

        await handler.HandleAsync(new CancelOrderCommand(TenantId, order.Id.Value, "Sin stock"), Ct);

        Assert.Equal(["cancelled", "wholesale.cancelled"], events.Published.Select(entry => entry.Event));
        Assert.Equal([(order.Id.Value, false)], events.WholesaleCancelled);
    }

    [Fact]
    public async Task CancellingAManualOrderDoesNotPublishWholesaleCancelled()
    {
        var order = Manual(new OrderLineInput(Kept, Kept, 2m, 20_000m, 0m, 0));
        var events = new RecordingQuotationLifecycleEventPublisher();
        var handler = new CancelOrderHandler(
            new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), new RecordingQuotationAuditPublisher(), events,
            new StubMembershipDirectory(Guid.CreateVersion7()), new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now),
            new NeverOpenExternalInvoice());

        await handler.HandleAsync(new CancelOrderCommand(TenantId, order.Id.Value, "Sin stock"), Ct);

        Assert.Equal(["cancelled"], events.Published.Select(entry => entry.Event));
    }

    private sealed class NeverOpenExternalInvoice : IExternalInvoiceStatus
    {
        public Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
