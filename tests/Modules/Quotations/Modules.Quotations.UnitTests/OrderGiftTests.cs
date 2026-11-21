using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Las líneas de regalo del pedido (spec 2026-11-21): descuento fijo en 100 %, precio real conservado, y la clave de
/// la línea es (variante, regalo), así que un regalo puede ser de la misma variante que se vende.</summary>
public sealed class OrderGiftTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(ClientId, Advisor, "COP", "Transferencia", false, null, false, []);

    private static OrderLineInput Line(Guid variantId, decimal quantity = 10m) =>
        new(variantId, Guid.CreateVersion7(), quantity, 1_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Order NewOrder(params OrderLineInput[] lines) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, Account,
            ShippingDetails.None, lines, null, Advisor, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static Order NewShopifyOrder(params OrderLineInput[] lines) =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, lines,
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1001", ShippingDetails.None, "shop:1001", null,
            OrderLineCosts.None);

    private static void AddGift(Order order, Guid variantId, decimal quantity = 1m, OrderLineCosts? costs = null) =>
        order.AddGift(
            OrderItemId.New(), variantId, Guid.CreateVersion7(), quantity, 1_000m, 19,
            costs ?? OrderLineCosts.None, Advisor, Now);

    [Fact]
    public void AGiftKeepsTheListPriceAndChargesNothing()
    {
        var order = NewOrder(Line(V1));

        AddGift(order, V2);

        var gift = Assert.Single(order.Items, item => item.VariantId == V2);
        Assert.True(gift.IsGift);
        Assert.Equal(1_000m, gift.UnitPrice);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(0m, gift.TaxAmount);
    }

    [Fact]
    public void AGiftOfTheSameVariantAsAPaidLineIsAllowed()
    {
        var order = NewOrder(Line(V1));
        var before = order.ProductsTotal;

        AddGift(order, V1);

        Assert.Equal(2, order.Items.Count);
        Assert.Equal(before, order.ProductsTotal);
        Assert.Single(order.Items, item => item.IsGift);
        Assert.Single(order.Items, item => !item.IsGift);
    }

    [Fact]
    public void TwoGiftsOfTheSameVariantAreRejected()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);

        var error = Assert.Throws<QuotationsDomainException>(() => AddGift(order, V2));

        Assert.Equal("order.item.duplicate_product", error.Code);
    }

    [Fact]
    public void TwoPaidLinesOfTheSameVariantAreStillRejected()
    {
        var order = NewOrder(Line(V1));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddItem(
                OrderItemId.New(), V1, Guid.CreateVersion7(), 1m, 1_000m, 0m, 19, OrderLineCosts.None, Advisor, Now));

        Assert.Equal("order.item.duplicate_product", error.Code);
    }

    [Fact]
    public void AGiftOnANonEditableOrderIsRejected()
    {
        var order = NewOrder(Line(V1));
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Cancel(Advisor, "Cliente desistió", Now);
        var expected = Assert.Throws<QuotationsDomainException>(() =>
            order.AddItem(
                OrderItemId.New(), V2, Guid.CreateVersion7(), 1m, 1_000m, 0m, 19, OrderLineCosts.None, Advisor, Now));

        var error = Assert.Throws<QuotationsDomainException>(() => AddGift(order, V2));

        Assert.Equal(expected.Code, error.Code);
    }

    [Fact]
    public void ApplyGroupDiscountsNeverLowersAGiftBelow100()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);
        var gift = order.Items.Single(item => item.IsGift);

        order.ApplyGroupDiscounts(
            new Dictionary<OrderItemId, QuotationItemDiscount>
            {
                [gift.Id] = new(0m, QuotationDiscountOrigin.Own),
            },
            Now);

        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
    }

    [Fact]
    public void ReplaceExternalLinesKeepsTheGifts()
    {
        var paid = Line(V1);
        var order = NewShopifyOrder(paid);
        AddGift(order, V1);
        AddGift(order, V2);

        var result = order.ApplyExternalEdit(
            new ExternalOrderEdit([paid with { Quantity = 3m }], null, null, false, null, "x"),
            OrderLineCosts.None, Now.AddHours(1));

        Assert.True(result.LinesChanged);
        Assert.Equal(2, order.Items.Count(item => item.IsGift));
        var kept = Assert.Single(order.Items, item => !item.IsGift);
        Assert.Equal(3m, kept.Quantity);
    }

    [Fact]
    public void AGiftOnAPaidOrderFreezesItsCost()
    {
        var order = NewOrder(Line(V1));
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        var costs = OrderLineCosts.Of([V2], new Dictionary<Guid, decimal> { [V2] = 400m });

        AddGift(order, V2, costs: costs);

        var gift = order.Items.Single(item => item.IsGift);
        Assert.Equal(400m, gift.UnitCost);
        Assert.Equal(Now, gift.CostCapturedAt);
    }
}
