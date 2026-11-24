using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El regalo de un pedido frente al resto del pedido: la edición de líneas pagadas y el repricing no tropiezan con él.
/// El alta, el cambio y la baja del regalo viajan en la lista <c>gifts</c> (<c>OrderGiftEditsTests</c>).</summary>
public sealed class OrderGiftPricingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
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

    // El catálogo cobra 10 % desde 10 unidades: el regalo no debe tomarlo, ni siquiera por la cantidad.
    private static QuotationPriceScaleRef TenPercentFromTen { get; } =
        new(10, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, []);

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
}
