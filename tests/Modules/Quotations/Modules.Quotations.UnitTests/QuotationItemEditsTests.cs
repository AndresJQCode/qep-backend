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

    private static QuotationVariantPricingRef Product(
        Guid productId, string name, decimal unitPriceCop, bool isActive = true) =>
        new(productId, productId, TenantId, name, "SKU", isActive, new Dictionary<string, decimal> { ["COP"] = unitPriceCop }, [], null);

    /// <summary>Un borrador con líneas sin impuesto ni descuento: el total es la suma de precio por
    /// cantidad y las aserciones no dependen de redondeos.</summary>
    private static Quotation EditableQuotation(
        params (Guid ProductId, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), UpdatedBy,
            new DateOnly(2026, 9, 30), null, null, QuotationParties.Empty, BillingAccount,
            "COP",
            false, false, UpdatedBy, Now);
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            quotation.AddItem(QuotationItemId.New(), productId, productId, quantity, unitPrice, 0m, 0, UpdatedBy, Now);
        }

        return quotation;
    }

    private static Task<IReadOnlyList<QuotationItemEdit>> ApplyAsync(
        Quotation quotation, StubPricingCatalog catalog, params QuotationItemAddition[] desired) =>
        QuotationItemEdits.ApplyAsync(
            quotation, desired, catalog, TenantId, UpdatedBy, Now.AddDays(1), isRetail: false,
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
        quotation.ConvertToOrder(UpdatedBy, Now, QuotationProductSnapshot.None);
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new QuotationItemAddition(productId, 5m)));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    // ---- Por variante (spec 2026-10-20 §4) ----

    private static readonly Guid Camiseta = Guid.CreateVersion7();
    private static readonly Guid CamisetaM = Guid.CreateVersion7();
    private static readonly Guid CamisetaL = Guid.CreateVersion7();

    private static QuotationVariantPricingRef Variant(Guid variantId, string size, decimal unitPriceCop) =>
        new(variantId, Camiseta, TenantId, $"Camiseta – {size}", $"CAM-{size}", true,
            new Dictionary<string, decimal> { ["COP"] = unitPriceCop }, [], null);

    // Dos tallas del mismo producto son dos líneas: el diff empareja por variante, y cada línea
    // guarda su variante y el producto común.
    [Fact]
    public async Task AddItemTwoSizesOfSameProductAreTwoLines()
    {
        var quotation = EditableQuotation();
        var catalog = new StubPricingCatalog(Variant(CamisetaM, "M", 30_000m), Variant(CamisetaL, "L", 32_000m));

        var edits = await ApplyAsync(
            quotation, catalog, new QuotationItemAddition(CamisetaM, 2m), new QuotationItemAddition(CamisetaL, 3m));

        Assert.Equal(
            [
                new QuotationItemEdit(QuotationItemEditKind.Added, CamisetaM, "Camiseta – M", null, 2m),
                new QuotationItemEdit(QuotationItemEditKind.Added, CamisetaL, "Camiseta – L", null, 3m),
            ],
            edits);
        Assert.Equal(
            [(CamisetaM, Camiseta, 30_000m), (CamisetaL, Camiseta, 32_000m)],
            quotation.Items.OrderBy(item => item.Position).Select(item => (item.VariantId, item.ProductId, item.UnitPrice)));
        Assert.Equal(2m * 30_000m + 3m * 32_000m, quotation.Total);
    }

    // La misma variante dos veces sigue siendo el duplicado de siempre, con el código que el
    // frontend ya conoce.
    [Fact]
    public void AddItemSameVariantTwiceReturnsDuplicateProductCode()
    {
        var quotation = EditableQuotation();
        quotation.AddItem(QuotationItemId.New(), CamisetaM, Camiseta, 1m, 30_000m, 0m, 0, UpdatedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), CamisetaM, Camiseta, 2m, 30_000m, 0m, 0, UpdatedBy, Now));

        Assert.Equal("quotation.item.duplicate_product", error.Code);
        Assert.Single(quotation.Items);
    }

    // Con M y L guardadas, la lista deseada «M x5» cambia la cantidad de M y quita L: la talla que
    // se queda no se confunde con la que se va aunque compartan producto.
    [Fact]
    public async Task TheDiffMatchesLinesByVariantNotByProduct()
    {
        var quotation = EditableQuotation();
        quotation.AddItem(QuotationItemId.New(), CamisetaM, Camiseta, 1m, 30_000m, 0m, 0, UpdatedBy, Now);
        quotation.AddItem(QuotationItemId.New(), CamisetaL, Camiseta, 1m, 32_000m, 0m, 0, UpdatedBy, Now);
        var catalog = new StubPricingCatalog(Variant(CamisetaM, "M", 30_000m), Variant(CamisetaL, "L", 32_000m));

        var edits = await ApplyAsync(quotation, catalog, new QuotationItemAddition(CamisetaM, 5m));

        Assert.Equal(
            [
                new QuotationItemEdit(QuotationItemEditKind.QuantityChanged, CamisetaM, "Camiseta – M", 1m, 5m),
                new QuotationItemEdit(QuotationItemEditKind.Removed, CamisetaL, null, 1m, null),
            ],
            edits);
        var line = Assert.Single(quotation.Items);
        Assert.Equal((CamisetaM, 5m), (line.VariantId, line.Quantity));
    }

    private sealed class StubPricingCatalog(params QuotationVariantPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        public List<Guid> Lookups { get; } = [];

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
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>>(
                products
                    .Where(product => productIds.Contains(product.VariantId))
                    .ToDictionary(product => product.VariantId));
    }
}
