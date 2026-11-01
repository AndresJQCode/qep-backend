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
        Assert.Empty(catalog.ManyLookups);
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
        Assert.Empty(catalog.ManyLookups);
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

    // ---- Una sola lectura del catálogo por guardado (N+1 de /quotations/preview) ----

    // El cálculo previo corre mientras la persona escribe: una lectura por línea eran hasta 130
    // sentencias por request en producción. La línea que no cambia no se pide.
    [Fact]
    public async Task PricesAdditionsAndQuantityChangesWithOneBatchedLookup()
    {
        var unchanged = Guid.CreateVersion7();
        var changed = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var quotation = EditableQuotation((unchanged, 1m, 100_000m), (changed, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(unchanged, "Vela de soja", 100_000m),
            Product(changed, "Vela de cera", 100_000m),
            Product(first, "Difusor", 50_000m),
            Product(second, "Incienso", 10_000m));

        var edits = await ApplyAsync(
            quotation,
            catalog,
            new QuotationItemAddition(unchanged, 1m),
            new QuotationItemAddition(first, 2m),
            new QuotationItemAddition(changed, 4m),
            new QuotationItemAddition(second, 3m));

        Assert.Empty(catalog.Lookups);
        Assert.Equal(
            new[] { changed, first, second }.Order(),
            Assert.Single(catalog.ManyLookups).Order());
        Assert.Equal(
            [
                new QuotationItemEdit(QuotationItemEditKind.Added, first, "Difusor", null, 2m),
                new QuotationItemEdit(QuotationItemEditKind.Added, second, "Incienso", null, 3m),
                new QuotationItemEdit(QuotationItemEditKind.QuantityChanged, changed, "Vela de cera", 1m, 4m),
            ],
            edits);
        Assert.Equal(100_000m + 400_000m + 100_000m + 30_000m, quotation.Total);
    }

    [Fact]
    public async Task DoesNotReadTheCatalogWhenNoLineNeedsPricing()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = EditableQuotation((kept, 1m, 100_000m), (removed, 2m, 50_000m));
        var catalog = new StubPricingCatalog(Product(kept, "Vela de soja", 100_000m));

        await ApplyAsync(quotation, catalog, new QuotationItemAddition(kept, 1m));

        Assert.Empty(catalog.Lookups);
        Assert.Empty(catalog.ManyLookups);
    }

    // Traer los precios de una vez no adelanta los rechazos: siguen saliendo línea por línea, en el
    // mismo punto, así que lo anterior a la línea mala queda aplicado y lo posterior no se toca.
    [Fact]
    public async Task TheFirstRejectedAdditionStopsEverythingAfterIt()
    {
        var existing = Guid.CreateVersion7();
        var valid = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var afterTheBadLine = Guid.CreateVersion7();
        var quotation = EditableQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m),
            Product(valid, "Difusor", 50_000m),
            Product(inactive, "Incienso", 10_000m, isActive: false),
            Product(afterTheBadLine, "Vela de cera", 20_000m));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ApplyAsync(
            quotation,
            catalog,
            new QuotationItemAddition(existing, 5m),
            new QuotationItemAddition(valid, 1m),
            new QuotationItemAddition(inactive, 1m),
            new QuotationItemAddition(afterTheBadLine, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal(
            [(existing, 1m), (valid, 1m)],
            quotation.Items.OrderBy(item => item.Position).Select(item => (item.VariantId, item.Quantity)));
    }

    // Las altas van primero aunque el cambio esté antes en la lista: el error que sale es el del alta.
    [Fact]
    public async Task ARejectedAdditionWinsOverARejectedChangeListedBeforeIt()
    {
        var inactiveExisting = Guid.CreateVersion7();
        var missing = Guid.CreateVersion7();
        var quotation = EditableQuotation((inactiveExisting, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(inactiveExisting, "Vela de soja", 100_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ApplyAsync(
            quotation,
            catalog,
            new QuotationItemAddition(inactiveExisting, 5m),
            new QuotationItemAddition(missing, 1m)));

        Assert.Equal("quotation.item.product_not_found", error.Code);
        Assert.Equal(1m, Assert.Single(quotation.Items).Quantity);
    }

    [Fact]
    public async Task TheFirstRejectedChangeStopsLaterChangesAndTheRemovals()
    {
        var valid = Guid.CreateVersion7();
        var noPrice = Guid.CreateVersion7();
        var afterTheBadLine = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = EditableQuotation(
            (valid, 1m, 100_000m), (noPrice, 1m, 100_000m), (afterTheBadLine, 1m, 100_000m), (removed, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(valid, "Vela de soja", 100_000m),
            new QuotationVariantPricingRef(
                noPrice, noPrice, TenantId, "Difusor", "SKU", true,
                new Dictionary<string, decimal> { ["USD"] = 25m }, [], null),
            Product(afterTheBadLine, "Incienso", 100_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ApplyAsync(
            quotation,
            catalog,
            new QuotationItemAddition(valid, 2m),
            new QuotationItemAddition(noPrice, 2m),
            new QuotationItemAddition(afterTheBadLine, 2m)));

        Assert.Equal("quotation.item.product_price_unavailable", error.Code);
        Assert.Equal(
            [(valid, 2m), (noPrice, 1m), (afterTheBadLine, 1m), (removed, 1m)],
            quotation.Items.OrderBy(item => item.Position).Select(item => (item.VariantId, item.Quantity)));
    }

    // La misma respuesta para "no existe" y "es de otro tenant", también por la lectura de varias.
    [Fact]
    public async Task AVariantOfAnotherTenantIsNotFound()
    {
        var foreign = Guid.CreateVersion7();
        var quotation = EditableQuotation();
        var catalog = new StubPricingCatalog(
            new QuotationVariantPricingRef(
                foreign, foreign, Guid.CreateVersion7(), "Difusor", "SKU", true,
                new Dictionary<string, decimal> { ["COP"] = 50_000m }, [], null));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new QuotationItemAddition(foreign, 1m)));

        Assert.Equal("quotation.item.product_not_found", error.Code);
        Assert.Empty(quotation.Items);
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
