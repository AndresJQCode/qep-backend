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

    private static Order NewWholesaleOrder(params OrderLineInput[] lines)
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
            OrderId.New(), TenantId, "PED-2026-0042", Header(), parties, Account, lines, null, Advisor, Now,
            DateOnly.FromDateTime(Now.UtcDateTime), Guid.CreateVersion7(), Guid.CreateVersion7(), "OC-0007");
    }

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

    [Fact]
    public void RemovingTheLastPaidLineWithGiftsIsRejected()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);
        var paid = order.Items.Single(item => !item.IsGift);

        var error = Assert.Throws<QuotationsDomainException>(() => order.RemoveItem(paid.Id, Advisor, Now));

        Assert.Equal("order.item.last_item_required", error.Code);
        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void RemovingAGiftKeepsThePaidLines()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);
        var gift = order.Items.Single(item => item.IsGift);

        order.RemoveItem(gift.Id, Advisor, Now);

        Assert.Single(order.Items);
        Assert.False(order.Items.Single().IsGift);
    }

    [Fact]
    public void ExternalEditWithGiftsKeepsPositionsUnique()
    {
        var paid = Line(V1);
        var order = NewShopifyOrder(paid);
        AddGift(order, V2);
        AddGift(order, V1);

        order.ApplyExternalEdit(
            new ExternalOrderEdit([paid, Line(Guid.CreateVersion7()), Line(Guid.CreateVersion7())], null, null, false, null, "x"),
            OrderLineCosts.None, Now.AddHours(1));

        var positions = order.Items.Select(item => item.Position).ToArray();
        Assert.Equal(positions.Length, positions.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, positions.Length), positions.Order());
        Assert.All(order.Items.Where(item => item.IsGift), gift =>
            Assert.True(gift.Position > order.Items.Where(item => !item.IsGift).Max(item => item.Position)));
    }

    // Spec 2026-11-23: un pedido manual nace con sus regalos, también de la misma variante que se vende.
    [Fact]
    public void CreateManualTakesAPaidLineAndAGiftOfTheSameVariant()
    {
        var paid = Line(V1);
        var withoutGift = NewOrder(paid);

        var order = NewOrder(paid, Line(V1, 1m) with { IsGift = true });

        Assert.Equal(2, order.Items.Count);
        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(V1, gift.VariantId);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(1_000m, gift.UnitPrice);
        Assert.Equal(withoutGift.Total, order.Total);
    }

    [Fact]
    public void CreateManualRejectsTheSameVariantTwiceAsAGift()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(
            Line(V1),
            Line(V2, 1m) with { IsGift = true },
            Line(V2, 2m) with { IsGift = true }));

        Assert.Equal("order.item.duplicate_product", error.Code);
    }

    [Fact]
    public void AGiftOnAWholesaleOrderIsRejected()
    {
        var order = NewWholesaleOrder(Line(V1));

        var error = Assert.Throws<QuotationsDomainException>(() => AddGift(order, V2));

        Assert.Equal("order.gift.wholesale_not_supported", error.Code);
        Assert.Single(order.Items);
    }

    [Fact]
    public void UpdateQuantityOnAGiftKeepsThe100Discount()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);
        var gift = order.Items.Single(item => item.IsGift);

        order.UpdateItemQuantity(gift.Id, 3m, 0m, 19, Advisor, Now);

        Assert.Equal(3m, gift.Quantity);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(0m, gift.TaxAmount);
    }

    [Fact]
    public void ApplyDiscountCannotLowerAGift()
    {
        var order = NewOrder(Line(V1));
        AddGift(order, V2);
        var gift = order.Items.Single(item => item.IsGift);

        gift.ApplyDiscount(10m, QuotationDiscountOrigin.Own, order.Currency, order.PriceRounding, Now);

        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
    }

    [Fact]
    public void AnUnchangedExternalEditWithGiftsReportsNoLineChange()
    {
        var paid = Line(V1);
        var order = NewShopifyOrder(paid);
        AddGift(order, V2);

        var result = order.ApplyExternalEdit(
            new ExternalOrderEdit([paid], null, null, false, null, "x"), OrderLineCosts.None, Now.AddHours(1));

        Assert.False(result.LinesChanged);
        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void ASoldOrderExternalEditAddingAPaidLineOfAGiftedVariantFreezesItsCost()
    {
        var paid = Line(V1);
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003", Header(), QuotationParties.Empty, Account, [paid],
            OrderPaymentStatus.FullPaymentReceived, null, Advisor, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1002", ShippingDetails.None, "shop:1002", null,
            OrderLineCosts.Of([V1], new Dictionary<Guid, decimal>()));
        var costs = OrderLineCosts.Of([V2], new Dictionary<Guid, decimal> { [V2] = 400m });
        AddGift(order, V2, costs: costs);

        order.ApplyExternalEdit(
            new ExternalOrderEdit([paid, Line(V2, 2m)], null, null, false, null, "x"), costs, Now.AddHours(1));

        var newPaid = Assert.Single(order.Items, item => item.VariantId == V2 && !item.IsGift);
        Assert.Equal(400m, newPaid.UnitCost);
        Assert.NotNull(newPaid.CostCapturedAt);
    }
}
