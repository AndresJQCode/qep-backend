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

    private static QuotationVariantPricingRef Product(
        Guid productId, string name, decimal unitPriceCop, bool isActive = true) =>
        new(productId, productId, TenantId, name, "SKU", isActive, new Dictionary<string, decimal> { ["COP"] = unitPriceCop }, [], null);

    // Un pedido manual pendiente con esas líneas (sin IVA, para que el total sea la suma de los precios).
    private static Order PendingOrder(params (Guid ProductId, decimal Quantity, decimal UnitPrice, QuotationProductSnapshot? Product)[] lines) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), UpdatedBy, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            lines.Select(line => new OrderLineInput(line.ProductId, line.ProductId, line.Quantity, line.UnitPrice, 0m, 0, line.Product)).ToArray(),
            null, UpdatedBy, [], Now);

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
        Assert.Empty(catalog.ManyLookups);
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
        Assert.Equal([other], Assert.Single(catalog.ManyLookups));
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
        Assert.Empty(catalog.ManyLookups);
    }

    // Este paso corre mientras la persona escribe en el cálculo previo: una lectura por línea eran
    // varias sentencias por línea. Las líneas intactas no se leen.
    [Fact]
    public async Task ReadsTheCatalogOnceForTheWholeDiff()
    {
        var unchanged = Guid.CreateVersion7();
        var changed = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var order = PendingOrder((unchanged, 1m, 100_000m, null), (changed, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(
            Product(unchanged, "Vela de soja", 100_000m),
            Product(changed, "Vela de cera", 100_000m),
            Product(first, "Difusor", 50_000m),
            Product(second, "Incienso", 10_000m));

        var edits = await ApplyAsync(
            order,
            catalog,
            new OrderItemAddition(unchanged, 1m),
            new OrderItemAddition(first, 2m),
            new OrderItemAddition(changed, 4m),
            new OrderItemAddition(second, 3m));

        Assert.Empty(catalog.Lookups);
        Assert.Equal(new[] { changed, first, second }.Order(), Assert.Single(catalog.ManyLookups).Order());
        Assert.Equal(
            [
                new OrderItemEdit(OrderItemEditKind.Added, first, "Difusor", null, 2m),
                new OrderItemEdit(OrderItemEditKind.Added, second, "Incienso", null, 3m),
                new OrderItemEdit(OrderItemEditKind.QuantityChanged, changed, "Vela de cera", 1m, 4m),
            ],
            edits);
        Assert.Equal(100_000m + 400_000m + 100_000m + 30_000m, order.Total);
    }

    // Traer los precios de una vez no adelanta los rechazos: lo anterior a la línea mala queda
    // aplicado y lo posterior no se toca.
    [Fact]
    public async Task TheFirstRejectedAdditionStopsEverythingAfterIt()
    {
        var existing = Guid.CreateVersion7();
        var good = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var never = Guid.CreateVersion7();
        var order = PendingOrder((existing, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m),
            Product(good, "Difusor", 50_000m),
            Product(inactive, "Incienso", 10_000m, isActive: false),
            Product(never, "Jabón", 20_000m));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ApplyAsync(
            order,
            catalog,
            new OrderItemAddition(existing, 2m),
            new OrderItemAddition(good, 1m),
            new OrderItemAddition(inactive, 1m),
            new OrderItemAddition(never, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal([existing, good], order.Items.Select(item => item.ProductId));
        Assert.Equal(1m, order.Items.Single(item => item.ProductId == existing).Quantity);
    }

    // FindManyAsync no filtra por tenant en este doble: quien rechaza es el resolutor, línea por línea.
    [Fact]
    public async Task RejectsAVariantOfAnotherTenantAsNotFound()
    {
        var foreign = Guid.CreateVersion7();
        var existing = Guid.CreateVersion7();
        var order = PendingOrder((existing, 1m, 100_000m, null));
        var catalog = new StubPricingCatalog(
            new QuotationVariantPricingRef(
                foreign, foreign, Guid.CreateVersion7(), "Difusor", "SKU", true,
                new Dictionary<string, decimal> { ["COP"] = 50_000m }, [], null));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(order, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(foreign, 1m)));

        Assert.Equal("quotation.item.product_not_found", error.Code);
        Assert.Equal(existing, Assert.Single(order.Items).ProductId);
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

    // ---- Por variante (spec 2026-10-20 §4) ----

    private static readonly Guid Camiseta = Guid.CreateVersion7();
    private static readonly Guid CamisetaM = Guid.CreateVersion7();
    private static readonly Guid CamisetaL = Guid.CreateVersion7();

    private static QuotationVariantPricingRef Variant(Guid variantId, string size, decimal unitPriceCop) =>
        new(variantId, Camiseta, TenantId, $"Camiseta – {size}", $"CAM-{size}", true,
            new Dictionary<string, decimal> { ["COP"] = unitPriceCop }, [], null);

    // Gemela de la de la cotización: con M en el pedido, agregar L es una línea nueva y no una
    // cantidad más para M.
    [Fact]
    public async Task AddItemTwoSizesOfSameProductAreTwoLines()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), UpdatedBy, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            [new OrderLineInput(CamisetaM, Camiseta, 2m, 30_000m, 0m, 0)],
            null, UpdatedBy, [], Now);
        var catalog = new StubPricingCatalog(Variant(CamisetaM, "M", 30_000m), Variant(CamisetaL, "L", 32_000m));

        var edits = await ApplyAsync(
            order, catalog, new OrderItemAddition(CamisetaM, 2m), new OrderItemAddition(CamisetaL, 3m));

        Assert.Equal(new OrderItemEdit(OrderItemEditKind.Added, CamisetaL, "Camiseta – L", null, 3m), Assert.Single(edits));
        Assert.Equal(
            [(CamisetaM, Camiseta, 2m), (CamisetaL, Camiseta, 3m)],
            order.Items.OrderBy(item => item.Position).Select(item => (item.VariantId, item.ProductId, item.Quantity)));
    }

    [Fact]
    public void AddItemSameVariantTwiceReturnsDuplicateProductCode()
    {
        var order = PendingOrder((CamisetaM, 1m, 30_000m, null));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddItem(OrderItemId.New(), CamisetaM, Camiseta, 2m, 30_000m, 0m, 0, UpdatedBy, Now));

        Assert.Equal("order.item.duplicate_product", error.Code);
        Assert.Single(order.Items);
    }

    // Un pedido manual no puede nacer con la misma variante en dos líneas: el dominio lo frena con
    // el mismo código, aunque el validador de la API ya lo haya frenado antes.
    [Fact]
    public void CreateManualRejectsTheSameVariantTwice()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), UpdatedBy, "COP", null, IsRetail: false, null, false, false),
            QuotationParties.Empty, BillingAccount, ShippingDetails.None,
            [
                new OrderLineInput(CamisetaM, Camiseta, 1m, 30_000m, 0m, 0),
                new OrderLineInput(CamisetaM, Camiseta, 2m, 30_000m, 0m, 0),
            ],
            null, UpdatedBy, [], Now));

        Assert.Equal("order.item.duplicate_product", error.Code);
    }

    private sealed class StubPricingCatalog(params QuotationVariantPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        public List<Guid> Lookups { get; } = [];

        public List<IReadOnlyCollection<Guid>> ManyLookups { get; } = [];

        public Task<QuotationVariantPricingRef?> FindVariantAsync(
            Guid tenantId, Guid productId, CancellationToken cancellationToken)
        {
            Lookups.Add(productId);
            return Task.FromResult<QuotationVariantPricingRef?>(
                products.FirstOrDefault(product => product.VariantId == productId));
        }

        public Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken)
        {
            ManyLookups.Add(productIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>>(
                products
                    .Where(product => productIds.Contains(product.VariantId))
                    .ToDictionary(product => product.VariantId));
        }
    }
}
