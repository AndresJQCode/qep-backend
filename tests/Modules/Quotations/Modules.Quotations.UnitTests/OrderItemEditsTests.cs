using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-09-17 (editar pedido como borrador): el guardado recibe la lista **completa** de
/// productos deseada y el backend calcula la diferencia contra la cotización, por
/// <c>productId</c>, en el orden altas → cambios → bajas.
/// </summary>
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
        new(productId, TenantId, name, isActive, unitPriceCop, null, [], null);

    // Convertida por el camino real, con líneas sin impuesto ni descuento: el total es la suma de
    // precio por cantidad y las aserciones no dependen de redondeos.
    private static Quotation ConvertedQuotation(params (Guid ProductId, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), UpdatedBy,
            new DateOnly(2026, 9, 30), null, null, QuotationParties.Empty, BillingAccount,
            false, false, UpdatedBy, Now);
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            quotation.AddItem(QuotationItemId.New(), productId, quantity, unitPrice, 0m, 0, UpdatedBy, Now);
        }

        quotation.ConvertToOrder(UpdatedBy, Now);
        return quotation;
    }

    private static Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Quotation quotation, StubPricingCatalog catalog, params OrderItemAddition[] desired) =>
        OrderItemEdits.ApplyAsync(
            quotation, desired, catalog, TenantId, UpdatedBy, Now.AddDays(1), isRetail: false,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AddsProductsThatAreNotInTheQuotationPricedFromTheCatalog()
    {
        var existing = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(added, "Difusor", 50_000m));

        var edits = await ApplyAsync(
            quotation, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(added, 2m));

        var edit = Assert.Single(edits);
        Assert.Equal(new OrderItemEdit(OrderItemEditKind.Added, added, "Difusor", null, 2m), edit);
        Assert.Equal(2, quotation.Items.Count);
        Assert.Equal(200_000m, quotation.Total);
    }

    [Fact]
    public async Task RepricesAProductWhoseQuantityChanged()
    {
        var productId = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((productId, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(productId, 3m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.QuantityChanged, productId, "Vela de soja", 1m, 3m),
            Assert.Single(edits));
        Assert.Equal(3m, Assert.Single(quotation.Items).Quantity);
        Assert.Equal(300_000m, quotation.Total);
    }

    [Fact]
    public async Task RemovesProductsLeftOutOfTheDesiredList()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((kept, 1m, 100_000m), (removed, 2m, 50_000m));
        var catalog = new StubPricingCatalog(Product(kept, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(kept, 1m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.Removed, removed, null, 2m, null),
            Assert.Single(edits));
        Assert.Equal(kept, Assert.Single(quotation.Items).ProductId);
        Assert.Empty(catalog.Lookups);
    }

    // El orden del spec: bajar primero dispararía quotation.item.last_item_required a mitad de un
    // reemplazo total.
    [Fact]
    public async Task ReplacingEveryProductAddsBeforeRemovingSoTheLastItemRuleDoesNotFire()
    {
        var previous = Guid.CreateVersion7();
        var replacement = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((previous, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(replacement, "Difusor", 50_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(replacement, 1m));

        Assert.Equal(
            new[] { OrderItemEditKind.Added, OrderItemEditKind.Removed },
            edits.Select(edit => edit.Kind).ToArray());
        Assert.Equal(replacement, Assert.Single(quotation.Items).ProductId);
        Assert.Equal(50_000m, quotation.Total);
    }

    [Fact]
    public async Task LeavesTheQuotationUntouchedWhenNothingChanges()
    {
        var productId = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((productId, 2m, 100_000m));
        var versionBefore = quotation.Version;
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(productId, 2m));

        Assert.Empty(edits);
        Assert.Equal(versionBefore, quotation.Version);
        Assert.Empty(catalog.Lookups);
    }

    [Fact]
    public async Task RejectsAnInactiveNewProductWithTheCodeOfTheOtherEndpoints()
    {
        var existing = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(inactive, "Difusor", 50_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(inactive, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal(existing, Assert.Single(quotation.Items).ProductId);
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
