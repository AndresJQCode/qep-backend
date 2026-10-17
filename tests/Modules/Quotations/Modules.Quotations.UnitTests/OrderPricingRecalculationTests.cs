using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La pasada de descuentos de grupo sobre el pedido (espejo de <see cref="QuotationPricingRecalculation"/>, plan
/// 2026-10-15, P4). Un pedido ya está vendido: una línea cuyo producto el catálogo ya no devuelve no tiene escalas
/// contra las que recalcular, así que conserva el descuento con el que se vendió.
/// </summary>
public sealed class OrderPricingRecalculationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static QuotationProductPricingRef Product(Guid id) =>
        new(id, TenantId, "Producto", true, new Dictionary<string, decimal> { ["COP"] = 10_000m },
            [new QuotationPriceScaleRef(1, 999, 5m, QuotationPriceScaleRestriction.Multiple, 1, [], false)], 19);

    // Review Focus 3 a nivel de la pasada completa: editar otra línea no le resetea el descuento a la del producto borrado.
    [Fact]
    public async Task ALineWhoseProductIsGoneKeepsItsDiscount()
    {
        var gone = Guid.CreateVersion7();
        var kept = Guid.CreateVersion7();
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), AdvisorId, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            [
                new OrderLineInput(gone, 2m, 10_000m, 10m, 19, new QuotationProductSnapshot("OLD", "Viejo")),
                new OrderLineInput(kept, 10m, 10_000m, 0m, 19),
            ],
            OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);

        await OrderPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(kept)), new FixedQuotationSettingsStore(), TenantId, order, Now.AddDays(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (10m, 5m),
            (order.Items.Single(item => item.ProductId == gone).DiscountPercentage,
                order.Items.Single(item => item.ProductId == kept).DiscountPercentage));
    }

    // El envío cobrado suma al total del pedido, pero no es compra: no habilita el piso global de escala.
    [Fact]
    public async Task FreightDoesNotEnableTheGlobalFloor()
    {
        var productId = Guid.CreateVersion7();
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0002",
            new OrderHeader(Guid.CreateVersion7(), AdvisorId, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            [new OrderLineInput(productId, 1m, 150_000m, 5m, 19)],
            OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);
        order.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Charged, 60_000m, 19, false), AdvisorId, Now);
        var settings = QuotationSettings.Create(6, new Dictionary<string, decimal> { ["COP"] = 200_000m });

        await OrderPricingRecalculation.ApplyAsync(
            new StubPricingLookup(Product(productId)), new FixedQuotationSettingsStore(settings), TenantId, order,
            Now.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(0m, order.Items.Single().DiscountPercentage);
    }

    private sealed class StubPricingLookup(params QuotationProductPricingRef[] products) : IQuotationProductPricingLookup
    {
        private readonly Dictionary<Guid, QuotationProductPricingRef> _products = products.ToDictionary(product => product.Id);

        public Task<QuotationProductPricingRef?> FindAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_products.GetValueOrDefault(productId));

        public Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductPricingRef>>(
                productIds.Where(_products.ContainsKey).ToDictionary(id => id, id => _products[id]));
    }
}
