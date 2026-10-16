using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-09-17 y spec 2026-10-15 §4.5: las ediciones de líneas de «Editar» pedido se aplican
/// sobre el pedido, no sobre su cotización.</summary>
public sealed class OrderItemEditsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId UpdatedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static QuotationProductPricingRef Product(
        Guid productId, string name, decimal unitPriceCop, bool isActive = true) =>
        new(productId, TenantId, name, isActive, new Dictionary<string, decimal> { ["COP"] = unitPriceCop }, [], null);

    // Un pedido manual pendiente con esas líneas (sin IVA, para que el total sea la suma de los precios).
    private static Order PendingOrder(params (Guid ProductId, decimal Quantity, decimal UnitPrice, QuotationProductSnapshot? Product)[] lines) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), UpdatedBy, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            lines.Select(line => new OrderLineInput(line.ProductId, line.Quantity, line.UnitPrice, 0m, 0, line.Product)).ToArray(),
            OrderPaymentStatus.PaymentPending, null, UpdatedBy, [], Now);

    private static Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order, StubPricingCatalog catalog, params OrderItemAddition[] desired) =>
        OrderItemEdits.ApplyAsync(
            order, desired, catalog, TenantId, UpdatedBy, Now.AddDays(1), isRetail: false,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AddsProductsThatAreNotInTheOrderPricedFromTheCatalog()
    {
        var existing = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var order = PendingOrder((existing, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(added, "Difusor", 50_000m));

        var edits = await ApplyAsync(
            order, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(added, 2m));

        var edit = Assert.Single(edits);
        Assert.Equal(new OrderItemEdit(OrderItemEditKind.Added, added, "Difusor", null, 2m), edit);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(200_000m, order.Total);
    }

    [Fact]
    public async Task RepricesAProductWhoseQuantityChanged()
    {
        var productId = Guid.CreateVersion7();
        var order = PendingOrder((productId, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(order, catalog, new OrderItemAddition(productId, 3m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.QuantityChanged, productId, "Vela de soja", 1m, 3m),
            Assert.Single(edits));
        Assert.Equal(3m, Assert.Single(order.Items).Quantity);
        Assert.Equal(300_000m, order.Total);
    }

    [Fact]
    public async Task RemovesProductsLeftOutOfTheDesiredList()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var order = PendingOrder((kept, 1m, 100_000m, null), (removed, 2m, 50_000m, null));
        var catalog = new StubPricingCatalog(Product(kept, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(order, catalog, new OrderItemAddition(kept, 1m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.Removed, removed, null, 2m, null),
            Assert.Single(edits));
        Assert.Equal(kept, Assert.Single(order.Items).ProductId);
        Assert.Empty(catalog.Lookups);
    }

    [Fact]
    public async Task AppliesAdditionsQuantityChangesAndRemovalsOnTheOrder()
    {
        var keep = Guid.CreateVersion7();
        var drop = Guid.CreateVersion7();
        var add = Guid.CreateVersion7();
        var order = PendingOrder((keep, 1m, 10_000m, null), (drop, 2m, 10_000m, null));
        var catalog = new StubPricingCatalog(
            Product(keep, "Uno", 10_000m), Product(drop, "Dos", 10_000m), Product(add, "Tres", 10_000m));

        var edits = await ApplyAsync(
            order, catalog, new OrderItemAddition(keep, 3m), new OrderItemAddition(add, 1m));

        Assert.Equal(
            [OrderItemEditKind.Added, OrderItemEditKind.QuantityChanged, OrderItemEditKind.Removed],
            edits.Select(edit => edit.Kind));
        Assert.Equal([keep, add], order.Items.Select(item => item.ProductId));
        Assert.Equal(3m, order.Items.Single(item => item.ProductId == keep).Quantity);
    }

    // Review Focus 3 del plan: una línea intacta de un producto que ya no existe se queda con su foto.
    [Fact]
    public async Task LeavesAnUntouchedLineAloneEvenIfItsProductIsGone()
    {
        var gone = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var order = PendingOrder(
            (gone, 1m, 10_000m, new QuotationProductSnapshot("OLD", "Viejo")), (other, 1m, 10_000m, null));
        var catalog = new StubPricingCatalog(Product(other, "Otro", 10_000m));

        await ApplyAsync(order, catalog, new OrderItemAddition(gone, 1m), new OrderItemAddition(other, 2m));

        Assert.Equal("OLD", order.Items.Single(item => item.ProductId == gone).ProductCode);
        Assert.Equal([other], catalog.Lookups);
    }

    [Fact]
    public async Task ReplacingEveryProductAddsBeforeRemovingSoTheLastItemRuleDoesNotFire()
    {
        var previous = Guid.CreateVersion7();
        var replacement = Guid.CreateVersion7();
        var order = PendingOrder((previous, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(Product(replacement, "Difusor", 50_000m));

        var edits = await ApplyAsync(order, catalog, new OrderItemAddition(replacement, 1m));

        Assert.Equal(
            new[] { OrderItemEditKind.Added, OrderItemEditKind.Removed },
            edits.Select(edit => edit.Kind).ToArray());
        Assert.Equal(replacement, Assert.Single(order.Items).ProductId);
        Assert.Equal(50_000m, order.Total);
    }

    [Fact]
    public async Task LeavesTheOrderUntouchedWhenNothingChanges()
    {
        var productId = Guid.CreateVersion7();
        var order = PendingOrder((productId, 2m, 100_000m, null));
        var versionBefore = order.Version;
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(order, catalog, new OrderItemAddition(productId, 2m));

        Assert.Empty(edits);
        Assert.Equal(versionBefore, order.Version);
        Assert.Empty(catalog.Lookups);
    }

    [Fact]
    public async Task RejectsAnInactiveNewProductWithTheCodeOfTheOtherEndpoints()
    {
        var existing = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var order = PendingOrder((existing, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(inactive, "Difusor", 50_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(order, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(inactive, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal(existing, Assert.Single(order.Items).ProductId);
    }

    private sealed class StubPricingCatalog(params QuotationProductPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        public List<Guid> Lookups { get; } = [];

        public Task<QuotationProductPricingRef?> FindAsync(
            Guid tenantId, Guid productId, CancellationToken cancellationToken)
        {
            Lookups.Add(productId);
            return Task.FromResult<QuotationProductPricingRef?>(
                products.FirstOrDefault(product => product.Id == productId));
        }

        public Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductPricingRef>>(
                products
                    .Where(product => productIds.Contains(product.Id))
                    .ToDictionary(product => product.Id));
    }
}
