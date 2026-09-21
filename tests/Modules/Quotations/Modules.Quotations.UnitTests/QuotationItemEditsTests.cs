using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El guardado de una vez de una cotización editable recibe la lista **completa** de productos
/// deseada y el backend calcula la diferencia contra lo guardado, por <c>productId</c> — gemelo de
/// <see cref="OrderItemEditsTests"/>, pero por el camino editable (Draft/Sent), que usa
/// <c>AddItem</c>/<c>UpdateItemQuantity</c>/<c>RemoveItem</c> y no las variantes
/// <c>*AfterConversion</c>.
/// </summary>
public sealed class QuotationItemEditsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId UpdatedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>Un borrador con líneas sin impuesto ni descuento: el total es la suma de precio por
    /// cantidad y las aserciones no dependen de redondeos.</summary>
    private static Quotation EditableQuotation(
        params (Guid ProductId, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), UpdatedBy,
            new DateOnly(2026, 9, 30), null, null, QuotationParties.Empty, BillingAccount,
            false, false, UpdatedBy, Now);
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            quotation.AddItem(QuotationItemId.New(), productId, quantity, unitPrice, 0m, 0, UpdatedBy, Now);
        }

        return quotation;
    }

    private static Task<IReadOnlyList<QuotationItemEdit>> ApplyAsync(
        Quotation quotation, StubPricingCatalog catalog, params QuotationItemAddition[] desired) =>
        QuotationItemEdits.ApplyAsync(
            quotation, desired, catalog, TenantId, UpdatedBy, Now.AddDays(1),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AddsProductsThatAreNotInTheQuotationPricedFromTheCatalog()
    {
        var existing = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var quotation = EditableQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(added, "Difusor", 50_000m));

        var edits = await ApplyAsync(
            quotation, catalog, new QuotationItemAddition(existing, 1m), new QuotationItemAddition(added, 2m));

        Assert.Equal(
            new QuotationItemEdit(QuotationItemEditKind.Added, added, "Difusor", null, 2m),
            Assert.Single(edits));
        Assert.Equal(2, quotation.Items.Count);
        Assert.Equal(200_000m, quotation.Total);
    }

    [Fact]
    public async Task RepricesAProductWhoseQuantityChanged()
    {
        var productId = Guid.CreateVersion7();
        var quotation = EditableQuotation((productId, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new QuotationItemAddition(productId, 3m));

        Assert.Equal(
            new QuotationItemEdit(QuotationItemEditKind.QuantityChanged, productId, "Vela de soja", 1m, 3m),
            Assert.Single(edits));
        Assert.Equal(3m, Assert.Single(quotation.Items).Quantity);
        Assert.Equal(300_000m, quotation.Total);
    }

    [Fact]
    public async Task RemovesProductsLeftOutOfTheDesiredList()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = EditableQuotation((kept, 1m, 100_000m), (removed, 2m, 50_000m));
        var catalog = new StubPricingCatalog(Product(kept, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new QuotationItemAddition(kept, 1m));

        Assert.Equal(
            new QuotationItemEdit(QuotationItemEditKind.Removed, removed, null, 2m, null),
            Assert.Single(edits));
        Assert.Equal(kept, Assert.Single(quotation.Items).ProductId);
        Assert.Empty(catalog.Lookups);
    }

    // El borrador sí puede quedarse sin líneas —nace así— y RemoveItem no tiene la regla del
    // último ítem que sí tiene RemoveItemAfterConversion.
    [Fact]
    public async Task ClearsEveryLineWhenTheDesiredListIsEmpty()
    {
        var productId = Guid.CreateVersion7();
        var quotation = EditableQuotation((productId, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog);

        Assert.Equal(QuotationItemEditKind.Removed, Assert.Single(edits).Kind);
        Assert.Empty(quotation.Items);
        Assert.Equal(0m, quotation.Total);
    }

    // El orden elegido para este camino: altas antes que bajas. AddItemCore numera la línea nueva
    // con `_items.Count + 1`, así que bajar primero le daría a la nueva la posición que todavía
    // ocupa una línea que se queda.
    [Fact]
    public async Task AddsBeforeRemovingSoTheNewLineNeverTakesThePositionOfOneThatStays()
    {
        var removed = Guid.CreateVersion7();
        var kept = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var quotation = EditableQuotation((removed, 1m, 100_000m), (kept, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(kept, "Vela de soja", 100_000m), Product(added, "Difusor", 50_000m));

        var edits = await ApplyAsync(
            quotation, catalog, new QuotationItemAddition(kept, 1m), new QuotationItemAddition(added, 1m));

        Assert.Equal(
            new[] { QuotationItemEditKind.Added, QuotationItemEditKind.Removed },
            edits.Select(edit => edit.Kind).ToArray());
        var positions = quotation.Items.Select(item => item.Position).ToArray();
        Assert.Equal(positions.Length, positions.Distinct().Count());
    }

    [Fact]
    public async Task LeavesTheQuotationUntouchedWhenNothingChanges()
    {
        var productId = Guid.CreateVersion7();
        var quotation = EditableQuotation((productId, 2m, 100_000m));
        var versionBefore = quotation.Version;
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new QuotationItemAddition(productId, 2m));

        Assert.Empty(edits);
        Assert.Equal(versionBefore, quotation.Version);
        Assert.Empty(catalog.Lookups);
    }

    [Fact]
    public async Task RejectsAnInactiveNewProductWithTheCodeOfTheOtherEndpoints()
    {
        var existing = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var quotation = EditableQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(inactive, "Difusor", 50_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new QuotationItemAddition(existing, 1m), new QuotationItemAddition(inactive, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal(existing, Assert.Single(quotation.Items).ProductId);
    }

    // Una cotización ya convertida se edita por la ruta del pedido: acá el propio agregado la
    // frena con quotation.quotation.not_editable (Quotation.cs:851).
    [Fact]
    public async Task RefusesToTouchAConvertedQuotation()
    {
        var productId = Guid.CreateVersion7();
        var quotation = EditableQuotation((productId, 1m, 100_000m));
        quotation.ConvertToOrder(UpdatedBy, Now);
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new QuotationItemAddition(productId, 5m)));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
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
