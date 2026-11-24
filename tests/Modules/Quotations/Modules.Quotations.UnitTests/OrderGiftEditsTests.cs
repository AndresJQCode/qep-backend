using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Spec 2026-11-23: la conciliación de los regalos de «Editar pedido» (OrderGiftEdits).
public sealed class OrderGiftEditsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

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

    // Un pedido manual con una línea pagada de V1 x10 a 1.000, sin IVA.
    private static Order NewOrder() =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, []),
            QuotationParties.Empty, Account, ShippingDetails.None,
            [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
            null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static Order NewWholesaleOrder()
    {
        var party = new QuotationPartyDetails
        {
            Name = "Distribuidora Andina SAS",
            IdentificationNumber = "900123456",
            IdentificationType = PartyIdentificationType.Nit,
            PersonType = PartyPersonType.Juridica,
            Phone = "+573001112233",
            Email = "compras@andina.example",
            Address = "Calle 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };
        var parties = new QuotationParties(
            party, party with { IdentificationNumber = null, IdentificationType = null, PersonType = null }, BillingVatSurplus: false);
        return Order.CreateFromWholesale(
            OrderId.New(), TenantId, "PED-2026-0042",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, []),
            parties, Account,
            [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
            null, Advisor, Now, DateOnly.FromDateTime(Now.UtcDateTime), Guid.CreateVersion7(), Guid.CreateVersion7(), "OC-0007");
    }

    private static OrderItem AddGift(Order order, Guid variantId, decimal quantity)
    {
        var id = OrderItemId.New();
        order.AddGift(id, variantId, Guid.CreateVersion7(), quantity, 1_000m, 0, OrderLineCosts.None, Advisor, Now);
        order.CaptureProductSnapshots(
            new Dictionary<Guid, QuotationProductSnapshot> { [variantId] = new("ACE", "Aceite") });
        return order.FindItem(id);
    }

    private static Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order,
        IReadOnlyCollection<OrderItemAddition>? desired,
        IQuotationProductPricingLookup? catalog = null,
        RecordingOrderVariantCostLookup? costs = null) =>
        OrderGiftEdits.ApplyAsync(
            order, desired, catalog ?? new StubQuotationProductPricingLookup(Variant(V1, 1_000m, 0), Variant(V2, 2_000m, 0)),
            costs ?? new RecordingOrderVariantCostLookup(), TenantId, Advisor, Now.AddDays(1),
            TestContext.Current.CancellationToken);

    [Fact]
    public void GiftQuantityChangedNamesTheGift() =>
        Assert.Equal(
            "Cambió la cantidad del regalo Aceite: 1 → 3.",
            OrderChangeSummary.GiftQuantityChanged("Aceite", 1m, 3m, HistoryFormat.Colombian));

    [Fact]
    public async Task NullLeavesTheGiftsUntouched()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);

        var edits = await ApplyAsync(order, null);

        Assert.Empty(edits);
        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(2m, gift.Quantity);
    }

    // Spec 2026-11-22 (D-5): el regalo nuevo de un pedido de la tienda se precia con la lista del canal Store, no con la de cotizaciones.
    [Theory]
    [InlineData(true, QuotationPriceChannel.Store)]
    [InlineData(false, QuotationPriceChannel.Quotations)]
    public async Task AGiftIsPricedWithTheChannelOfTheOrderSource(bool storeOrder, QuotationPriceChannel expected)
    {
        var order = storeOrder
            ? Order.CreateFromStore(
                OrderId.New(), TenantId, "PED-2026-0050",
                new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, []),
                QuotationParties.Empty, Account,
                [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
                null, Advisor, Now, DateOnly.FromDateTime(Now.UtcDateTime), "store-checkout:50")
            : NewOrder();
        var catalog = new ChannelRecordingLookup(Variant(V2, 2_000m, 0));

        await ApplyAsync(order, [new OrderItemAddition(V2, 1m)], catalog);

        Assert.Equal(expected, Assert.Single(catalog.Channels));
    }

    private sealed class ChannelRecordingLookup(params QuotationVariantPricingRef[] products) : IQuotationProductPricingLookup
    {
        private readonly StubQuotationProductPricingLookup _inner = new(products);

        public List<QuotationPriceChannel> Channels { get; } = [];

        public Task<QuotationVariantPricingRef?> FindVariantAsync(Guid tenantId, Guid variantId, CancellationToken cancellationToken) =>
            _inner.FindVariantAsync(tenantId, variantId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
        {
            Channels.Add(QuotationPriceChannel.Quotations);
            return _inner.FindManyAsync(tenantId, variantIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, QuotationPriceChannel channel, CancellationToken cancellationToken)
        {
            Channels.Add(channel);
            return _inner.FindManyAsync(tenantId, variantIds, cancellationToken);
        }
    }

    [Fact]
    public async Task AddsAGiftAtListPriceWithFullDiscount()
    {
        var order = NewOrder();
        var catalog = new StubQuotationProductPricingLookup(
            Variant(V2, 2_000m, 5, new QuotationPriceScaleRef(10, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, [])));

        var edits = await ApplyAsync(order, [new OrderItemAddition(V2, 12m)], catalog);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(V2, gift.VariantId);
        Assert.Equal(12m, gift.Quantity);
        Assert.Equal(2_000m, gift.UnitPrice);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(5, gift.TaxPercentage);
        var edit = Assert.Single(edits);
        Assert.Equal(OrderItemEditKind.Added, edit.Kind);
        Assert.True(edit.IsGift);
        Assert.Equal(V2, edit.VariantId);
        Assert.Equal("Aceite", edit.ProductName);
        Assert.Equal(12m, edit.Quantity);
    }

    [Fact]
    public async Task AddsAGiftOfTheSameVariantAsAPaidLine()
    {
        var order = NewOrder();
        var paid = order.Items.Single();

        var edits = await ApplyAsync(order, [new OrderItemAddition(V1, 1m)]);

        Assert.Single(edits);
        Assert.Equal(2, order.Items.Count(item => item.VariantId == V1));
        Assert.Single(order.Items, item => item.IsGift && item.VariantId == V1);
        Assert.Equal(10m, paid.Quantity);
        Assert.False(paid.IsGift);
    }

    [Fact]
    public async Task ChangesTheQuantityWithoutRepricing()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);
        var catalog = new StubQuotationProductPricingLookup(Variant(V1, 9_999m, 0));

        var edits = await ApplyAsync(order, [new OrderItemAddition(V1, 5m)], catalog);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(5m, gift.Quantity);
        Assert.Equal(1_000m, gift.UnitPrice);
        Assert.Equal(100m, gift.DiscountPercentage);
        var edit = Assert.Single(edits);
        Assert.Equal(OrderItemEditKind.QuantityChanged, edit.Kind);
        Assert.True(edit.IsGift);
        Assert.Equal(2m, edit.PreviousQuantity);
        Assert.Equal(5m, edit.Quantity);
        Assert.Equal("Aceite", edit.ProductName);
    }

    [Fact]
    public async Task LeavesAnUnchangedGiftAlone()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);

        var edits = await ApplyAsync(order, [new OrderItemAddition(V1, 2m)]);

        Assert.Empty(edits);
        Assert.Equal(2m, Assert.Single(order.Items, item => item.IsGift).Quantity);
    }

    [Fact]
    public async Task RemovesAGiftMissingFromTheList()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);
        AddGift(order, V2, 1m);

        var edits = await ApplyAsync(order, [new OrderItemAddition(V2, 1m)]);

        Assert.Equal(2, order.Items.Count);
        Assert.Single(order.Items, item => item.IsGift && item.VariantId == V2);
        Assert.Single(order.Items, item => !item.IsGift);
        var edit = Assert.Single(edits);
        Assert.Equal(OrderItemEditKind.Removed, edit.Kind);
        Assert.True(edit.IsGift);
        Assert.Equal(V1, edit.VariantId);
        Assert.Equal("Aceite", edit.ProductName);
    }

    [Fact]
    public async Task AnEmptyListRemovesEveryGift()
    {
        var order = NewOrder();
        AddGift(order, V1, 2m);
        AddGift(order, V2, 1m);

        var edits = await ApplyAsync(order, []);

        Assert.Equal(2, edits.Count);
        Assert.All(edits, edit => Assert.Equal(OrderItemEditKind.Removed, edit.Kind));
        var paid = Assert.Single(order.Items);
        Assert.False(paid.IsGift);
    }

    [Fact]
    public async Task AWholesaleOrderRejectsAGift()
    {
        var order = NewWholesaleOrder();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(order, [new OrderItemAddition(V1, 1m)]));

        Assert.Equal("order.gift.wholesale_not_supported", error.Code);
        Assert.DoesNotContain(order.Items, item => item.IsGift);
    }

    [Fact]
    public async Task AGiftAddedToAPaidOrderFreezesItsCost()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        var costs = new RecordingOrderVariantCostLookup(new Dictionary<Guid, decimal> { [V2] = 400m });

        await ApplyAsync(order, [new OrderItemAddition(V2, 1m)], costs: costs);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(400m, gift.UnitCost);
        Assert.NotNull(gift.CostCapturedAt);
    }
}
