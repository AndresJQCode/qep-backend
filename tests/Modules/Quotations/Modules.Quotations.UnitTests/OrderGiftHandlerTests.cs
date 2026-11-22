using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>Alta y baja del regalo de un pedido (spec 2026-11-21): precio de lista del catálogo, la misma doble revisión de
/// tenant que <c>AddOrderItems</c>, y el resto del pedido (edición por lista y repricing) sin tropezar con él.</summary>
public sealed class OrderGiftHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount Account = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static QuotationVariantPricingRef Variant(
        Guid variantId, decimal listPrice, int tax, params QuotationPriceScaleRef[] scales) =>
        new(variantId, Guid.CreateVersion7(), TenantId, "Aceite", "ACE", true,
            new Dictionary<string, decimal> { ["COP"] = listPrice }, scales, tax);

    private static Order NewOrder(bool isRetail = false, params (Guid VariantId, decimal Quantity)[] lines) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, isRetail, null, false, []),
            QuotationParties.Empty, Account, ShippingDetails.None,
            (lines.Length == 0 ? [(V1, 10m)] : lines)
                .Select(line => new OrderLineInput(
                    line.VariantId, Guid.CreateVersion7(), line.Quantity, 1_000m, 0m, 0,
                    new QuotationProductSnapshot("ACE", "Aceite")))
                .ToArray(),
            null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static OrderItem AddGift(Order order, Guid variantId, decimal quantity = 1m)
    {
        var id = OrderItemId.New();
        order.AddGift(id, variantId, Guid.CreateVersion7(), quantity, 1_000m, 0, OrderLineCosts.None, Advisor, Now);
        return order.FindItem(id);
    }

    private sealed record Harness(
        StubOrderListRepository Orders,
        RecordingQuotationAuditPublisher Audit,
        AddOrderGiftHandler Add,
        RemoveOrderGiftHandler Remove);

    private static Harness NewHarness(
        Order order, IExecutionContext? executionContext = null, params QuotationVariantPricingRef[] catalog)
    {
        var orders = new StubOrderListRepository(order);
        var audit = new RecordingQuotationAuditPublisher();
        var context = executionContext ?? new StubExecutionContext(SubjectId, TenantId);
        var quotations = new StubQuotationListRepository();
        var unitOfWork = new NoOpQuotationsUnitOfWork();
        var clock = new FixedClock(Now);
        var tenantDirectory = new StubTenantDirectory();
        var membership = new StubMembershipDirectory(Advisor.Value);
        var add = new AddOrderGiftHandler(
            orders, quotations, unitOfWork, audit, new StubQuotationProductPricingLookup(catalog),
            new FixedQuotationSettingsStore(new QuotationSettings(6, new Dictionary<string, decimal>())),
            new FixedTenantPriceRounding(), new StubQuotationProductLookup(catalog.ToDictionary(
                product => product.VariantId,
                product => new QuotationVariantRef(
                    product.VariantId, product.ProductId, product.Name, product.Code, null, product.Scales))),
            membership, tenantDirectory, context, clock, new AddOrderGiftValidator(),
            new RecordingOrderVariantCostLookup());
        var remove = new RemoveOrderGiftHandler(
            orders, quotations, unitOfWork, audit, membership, context, clock, new RemoveOrderGiftValidator());
        return new Harness(orders, audit, add, remove);
    }

    // El catálogo cobra 10 % desde 10 unidades: el regalo no debe tomarlo, ni siquiera por la cantidad.
    private static QuotationPriceScaleRef TenPercentFromTen { get; } =
        new(10, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, []);

    [Fact]
    public async Task AddGiftUsesTheCatalogListPriceAndTheProductTax()
    {
        var order = NewOrder();
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 5, TenPercentFromTen));

        var detail = await harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 12m), TestContext.Current.CancellationToken);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(V2, gift.VariantId);
        Assert.Equal(12m, gift.Quantity);
        Assert.Equal(2_000m, gift.UnitPrice);
        Assert.Equal(5, gift.TaxPercentage);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Single(detail.Order.Items, line => line.IsGift);
        Assert.Single(detail.Order.Items, line => !line.IsGift);
    }

    [Fact]
    public async Task AddGiftRejectsAnOtherTenantOrder()
    {
        var order = NewOrder();
        var otherTenant = Guid.CreateVersion7();
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => harness.Add.HandleAsync(
            new AddOrderGiftCommand(otherTenant, order.Id.Value, V2, 1m), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.DoesNotContain(order.Items, item => item.IsGift);
        Assert.Empty(harness.Audit.Actions);

        var removal = await Assert.ThrowsAsync<RequestForbiddenException>(() => harness.Remove.HandleAsync(
            new RemoveOrderGiftCommand(otherTenant, order.Id.Value, Guid.CreateVersion7()),
            TestContext.Current.CancellationToken));
        Assert.Equal("authorization.denied", removal.Code);
    }

    [Fact]
    public async Task RemoveGiftDoesNotTouchThePaidLineOfTheSameVariant()
    {
        var order = NewOrder();
        var gift = AddGift(order, V1);
        var paid = order.Items.Single(item => !item.IsGift);
        var harness = NewHarness(order);

        await harness.Remove.HandleAsync(
            new RemoveOrderGiftCommand(TenantId, order.Id.Value, gift.Id.Value), TestContext.Current.CancellationToken);

        var remaining = Assert.Single(order.Items);
        Assert.Equal(paid.Id, remaining.Id);
        Assert.False(remaining.IsGift);
        Assert.Equal(10m, remaining.Quantity);

        // Una línea pagada no es un regalo: por esta ruta no se quita, y se dice igual que una que no existe.
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Remove.HandleAsync(
            new RemoveOrderGiftCommand(TenantId, order.Id.Value, paid.Id.Value), TestContext.Current.CancellationToken));
        Assert.Equal("order.item.not_found", error.Code);
        Assert.Single(order.Items);
    }

    [Fact]
    public async Task PutItemsWithTheSameVariantKeepsTheGift()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);
        var catalog = new StubQuotationProductPricingLookup(Variant(V1, 1_000m, 0));

        async Task<IReadOnlyList<OrderItemEdit>> PutAsync(params OrderItemAddition[] desired) =>
            await OrderItemEdits.ApplyAsync(
                order, desired, catalog, new RecordingOrderVariantCostLookup(), new StubWholesaleLinePricing(),
                TenantId, Advisor, Now.AddDays(1), isRetail: false, TestContext.Current.CancellationToken);

        var unchanged = await PutAsync(new OrderItemAddition(V1, 10m));
        Assert.Empty(unchanged);
        Assert.Equal(2, order.Items.Count);

        var resized = await PutAsync(new OrderItemAddition(V1, 12m));
        var edit = Assert.Single(resized);
        Assert.Equal(OrderItemEditKind.QuantityChanged, edit.Kind);
        Assert.Equal(12m, order.Items.Single(item => !item.IsGift).Quantity);
        Assert.Equal(2m, order.Items.Single(item => item.IsGift).Quantity);
    }

    [Fact]
    public async Task RepricingIgnoresGiftQuantityForScalesAndMinimumPurchase()
    {
        // Escala: 5 pagadas más 6 de regalo sumarían 11 y entrarían al tramo de 10; sólo cuentan las 5.
        var scaled = NewOrder(isRetail: false, (V1, 5m));
        var scaledGift = AddGift(scaled, V1, 6m);
        var lookup = new StubQuotationProductPricingLookup(Variant(V1, 1_000m, 0, TenPercentFromTen));
        var noMinimum = new FixedQuotationSettingsStore(new QuotationSettings(1, new Dictionary<string, decimal>()));

        await OrderPricingRecalculation.ApplyAsync(
            lookup, noMinimum, new FixedTenantPriceRounding(), TenantId, scaled, Now.AddDays(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, scaled.Items.Single(item => !item.IsGift).DiscountPercentage);
        Assert.Equal(100m, scaledGift.DiscountPercentage);

        // Mínimo de compra: 3 pagadas y 5 de regalo llegarían a las 6 unidades; sólo cuentan las 3, así que el descuento cae.
        var small = NewOrder(isRetail: false, (V1, 3m));
        var smallGift = AddGift(small, V1, 5m);
        var anyScale = new StubQuotationProductPricingLookup(
            Variant(V1, 1_000m, 0, new QuotationPriceScaleRef(1, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, [])));
        var sixUnits = new FixedQuotationSettingsStore(new QuotationSettings(6, new Dictionary<string, decimal>()));

        await OrderPricingRecalculation.ApplyAsync(
            anyScale, sixUnits, new FixedTenantPriceRounding(), TenantId, small, Now.AddDays(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0m, small.Items.Single(item => !item.IsGift).DiscountPercentage);
        Assert.Equal(100m, smallGift.DiscountPercentage);
    }

    [Fact]
    public async Task AGiftWritesHistoryAndAudit()
    {
        var order = NewOrder();
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));

        await harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 3m), TestContext.Current.CancellationToken);

        var added = Assert.Single(harness.Orders.History);
        Assert.Equal(OrderHistoryEventType.ItemsChanged, added.EventType);
        Assert.Equal("Agregó de regalo Aceite x3.", added.Details);
        Assert.Equal(["quotation.order.gift_added"], harness.Audit.Actions);

        var giftId = order.Items.Single(item => item.IsGift).Id.Value;
        await harness.Remove.HandleAsync(
            new RemoveOrderGiftCommand(TenantId, order.Id.Value, giftId), TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Orders.History.Count);
        Assert.Equal(OrderHistoryEventType.ItemsChanged, harness.Orders.History[1].EventType);
        Assert.Equal("Quitó el regalo Aceite.", harness.Orders.History[1].Details);
        Assert.Equal(["quotation.order.gift_added", "quotation.order.gift_removed"], harness.Audit.Actions);
    }

    [Fact]
    public async Task AddGiftOnANonEditableOrderIsRejected()
    {
        var order = NewOrder();
        order.Cancel(Advisor, "Cliente desistió", Now);
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 1m), TestContext.Current.CancellationToken));

        Assert.Equal(ExternalOrderErrors.NotEditable, error.Code);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
    }

    [Fact]
    public async Task ASecondGiftOfTheSameVariantIsRejected()
    {
        var order = NewOrder();
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));
        await harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 1m), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 1m), TestContext.Current.CancellationToken));

        Assert.Equal("order.item.duplicate_product", error.Code);
        Assert.Single(order.Items, item => item.IsGift);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AGiftWithQuantityZeroOrLessFailsValidation(int quantity)
    {
        var order = NewOrder();
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, quantity), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(order.Items, item => item.IsGift);
    }

    [Fact]
    public async Task AGiftOnAWholesaleOrderIsRejectedThroughTheHandler()
    {
        var order = WholesaleOrderFixtures.Wholesale(WholesaleOrderFixtures.Line(V1, 2m, 1_000m));
        var harness = NewHarness(order, null, Variant(V2, 2_000m, 0));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Add.HandleAsync(
            new AddOrderGiftCommand(TenantId, order.Id.Value, V2, 1m), TestContext.Current.CancellationToken));

        Assert.Equal("order.gift.wholesale_not_supported", error.Code);
        Assert.DoesNotContain(order.Items, item => item.IsGift);
        Assert.Empty(harness.Audit.Actions);
    }
}
