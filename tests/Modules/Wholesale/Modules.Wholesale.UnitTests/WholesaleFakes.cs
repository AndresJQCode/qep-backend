using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; init; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// Spec 2026-11-22: las listas de Catalog vistas desde Mayorista. El precio sigue la regla de Catalog (D-2): precio fijo tal cual,
/// si no el base con el ajuste y <see cref="Rounding"/>. Una lista sin id es la de la vista previa.
/// </summary>
internal sealed class FakeWholesalePriceLists : IWholesalePriceLists
{
    public Dictionary<PriceListId, WholesalePriceList> Lists { get; } = [];

    /// <summary>Por tenant: una lista de otro tenant no se encuentra.</summary>
    public Dictionary<PriceListId, Guid> Owners { get; } = [];

    /// <summary>Precios fijos por lista, variante y moneda.</summary>
    public Dictionary<(PriceListId, Guid, string), decimal> Fixed { get; } = [];

    public PriceRounding Rounding { get; set; } = new(0, PriceRoundingMode.Nearest);

    public int Calls { get; private set; }

    public int ListCalls { get; private set; }

    public WholesalePriceList Seed(
        Guid tenantId,
        string name,
        decimal adjustmentPercent,
        IReadOnlyCollection<Guid>? visibleCategoryIds = null,
        bool showPrices = true,
        IReadOnlyCollection<Guid>? hiddenCategoryIds = null,
        IReadOnlyCollection<Guid>? hiddenProductIds = null)
    {
        var list = new WholesalePriceList(
            new PriceListId(Guid.CreateVersion7()), name, adjustmentPercent, visibleCategoryIds, hiddenCategoryIds ?? [],
            hiddenProductIds ?? [], showPrices);
        Lists[list.Id] = list;
        Owners[list.Id] = tenantId;
        return list;
    }

    public Task<WholesalePriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Lists.TryGetValue(id, out var list) && Owners[id] == tenantId ? list : null);
    }

    public Task<IReadOnlyList<WholesalePriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        ListCalls++;
        IReadOnlyList<WholesalePriceList> lists = Lists.Values.Where(list => Owners[list.Id] == tenantId).ToList();
        return Task.FromResult(lists);
    }

    public Task<IReadOnlyDictionary<Guid, WholesaleListedPrice>> PriceAsync(
        Guid tenantId,
        WholesalePriceList list,
        string currency,
        IReadOnlyDictionary<Guid, decimal> basePrices,
        CancellationToken cancellationToken)
    {
        Calls++;
        IReadOnlyDictionary<Guid, WholesaleListedPrice> prices = basePrices.ToDictionary(
            pair => pair.Key,
            pair => Fixed.TryGetValue((list.Id, pair.Key, currency), out var amount)
                ? new WholesaleListedPrice(amount, true)
                : new WholesaleListedPrice(Rounding.Apply(pair.Value * (1 + (list.AdjustmentPercent / 100m))), false));
        return Task.FromResult(prices);
    }
}

/// <summary>Un producto con una sola variante: lo que el lector real ya resolvió (vendible, nombre visible, precios).</summary>
internal sealed record FakeCatalogProduct(
    Guid VariantId,
    string Code,
    string Name,
    Guid? CategoryId,
    IReadOnlyDictionary<string, decimal> Prices,
    bool IsActive = true,
    decimal TaxRatePercent = 0m)
{
    public Guid ProductId { get; init; } = Guid.CreateVersion7();

    public string? ImageUrl { get; init; }
}

/// <summary>
/// El catálogo del mayorista en memoria. <see cref="SampleAsync"/> filtra como el adaptador (activo, con precio en la moneda,
/// en las categorías pedidas) pero sin expandir descendientes: eso es del adaptador y lo prueba la integración (Task 6).
/// </summary>
internal sealed class FakeCatalogReader : IWholesaleCatalogReader
{
    public Dictionary<Guid, WholesaleCategoryNode> Categories { get; } = [];

    public List<FakeCatalogProduct> Products { get; } = [];

    public int FindCategoriesCalls { get; private set; }

    public List<IReadOnlyCollection<Guid>?> SampleCategoryIds { get; } = [];

    /// <summary>Las exclusiones de cada llamada a SampleAsync, como llegaron.</summary>
    public List<(IReadOnlyCollection<Guid>? Categories, IReadOnlyCollection<Guid>? Products)> SampleExclusions { get; } = [];

    /// <summary>Las exclusiones de cada llamada a SearchSellableAsync, como llegaron.</summary>
    public List<(IReadOnlyCollection<Guid>? Categories, IReadOnlyCollection<Guid>? Products)> SearchExclusions { get; } = [];

    public Guid ProductIdOf(Guid variantId) => Products.Single(product => product.VariantId == variantId).ProductId;

    /// <summary>Como el adaptador: el producto está excluido, o su categoría o un ancestro lo está (eso es «con descendientes»).</summary>
    private bool IsExcluded(FakeCatalogProduct product, IReadOnlyCollection<Guid>? categories, IReadOnlyCollection<Guid>? products) =>
        products?.Contains(product.ProductId) == true
        || (categories is { Count: > 0 } && PathOf(product.CategoryId).Any(categories.Contains));

    public List<(string Currency, int Take)> SampleCalls { get; } = [];

    public Guid AddCategory(string name, Guid? parentId = null)
    {
        var id = Guid.CreateVersion7();
        Categories[id] = new WholesaleCategoryNode(id, name, parentId);
        return id;
    }

    public List<(string Currency, IReadOnlyCollection<Guid>? CategoryIds, string? Search, int Page, int PageSize)> SearchCalls { get; } = [];

    public Guid AddProduct(
        string code,
        string name,
        Guid? categoryId,
        decimal? cop,
        decimal? usd = null,
        bool isActive = true,
        decimal taxRatePercent = 0m,
        string? imageUrl = null)
    {
        var prices = new Dictionary<string, decimal>();
        if (cop is { } copPrice)
        {
            prices["COP"] = copPrice;
        }

        if (usd is { } usdPrice)
        {
            prices["USD"] = usdPrice;
        }

        var product = new FakeCatalogProduct(Guid.CreateVersion7(), code, name, categoryId, prices, isActive, taxRatePercent)
        {
            ImageUrl = imageUrl,
        };
        Products.Add(product);
        return product.VariantId;
    }

    /// <summary>La categoría y sus ancestros, como lo arma el lector real.</summary>
    private List<Guid> PathOf(Guid? categoryId)
    {
        var path = new List<Guid>();
        for (var current = categoryId; current is { } id && Categories.TryGetValue(id, out var node); current = node.ParentId)
        {
            path.Add(id);
        }

        return path;
    }

    private WholesaleSellableVariant Sellable(FakeCatalogProduct product, string currency) =>
        new(
            product.VariantId, product.Code, product.Name, product.Prices[currency], product.TaxRatePercent, PathOf(product.CategoryId),
            product.ProductId, "Producto " + product.Name, product.ImageUrl);

    public Task<IReadOnlyDictionary<Guid, WholesaleSellableVariant>> FindSellableAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, WholesaleSellableVariant> found = Products
            .Where(product => variantIds.Contains(product.VariantId) && product.IsActive && product.Prices.ContainsKey(currency))
            .ToDictionary(product => product.VariantId, product => Sellable(product, currency));
        return Task.FromResult(found);
    }

    /// <summary>Filtra como el adaptador: con categorías, las que tienen alguna en su camino (eso es «con descendientes»).</summary>
    public Task<(IReadOnlyList<WholesaleSellableVariant> Items, int Total)> SearchSellableAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        SearchCalls.Add((currency, categoryIds, search, page, pageSize));
        SearchExclusions.Add((excludedCategoryIds, excludedProductIds));
        var matching = Products
            .Where(product => product.IsActive && product.Prices.ContainsKey(currency))
            .Where(product => !IsExcluded(product, excludedCategoryIds, excludedProductIds))
            .Where(product => categoryIds is null || PathOf(product.CategoryId).Any(categoryIds.Contains))
            .Where(product => search is null
                || product.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || product.Code.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(product => product.Name, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<WholesaleSellableVariant> items = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(product => Sellable(product, currency))
            .ToList();
        return Task.FromResult((items, matching.Count));
    }

    public int FindProductsCalls { get; private set; }

    /// <summary>Ids de productos que el catálogo ya no tiene (borrados): <see cref="FindProductsAsync"/> no los devuelve.</summary>
    public HashSet<Guid> DeletedProducts { get; } = [];

    public Task<IReadOnlyDictionary<Guid, WholesaleProductRef>> FindProductsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        FindProductsCalls++;
        IReadOnlyDictionary<Guid, WholesaleProductRef> found = Products
            .Where(product => ids.Contains(product.ProductId) && !DeletedProducts.Contains(product.ProductId))
            .ToDictionary(
                product => product.ProductId,
                product => new WholesaleProductRef(product.ProductId, product.Code, product.Name, product.CategoryId, 1, product.ImageUrl));
        return Task.FromResult(found);
    }

    public List<(Guid? CategoryId, string? Search, int Page, int PageSize)> ProductSearchCalls { get; } = [];

    /// <summary>Activos con precio en la moneda; <c>categoryId</c> con descendientes (el camino de la categoría lo contiene).</summary>
    public Task<(IReadOnlyList<WholesaleProductRef> Items, int Total)> SearchProductsAsync(
        Guid tenantId, string currency, Guid? categoryId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        ProductSearchCalls.Add((categoryId, search, page, pageSize));
        var matching = Products
            .Where(product => product.IsActive && product.Prices.ContainsKey(currency))
            .Where(product => categoryId is null || PathOf(product.CategoryId).Contains(categoryId.Value))
            .Where(product => search is null
                || product.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || product.Code.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(product => product.Name, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<WholesaleProductRef> items = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(product => new WholesaleProductRef(product.ProductId, product.Code, product.Name, product.CategoryId, 1, product.ImageUrl))
            .ToList();
        return Task.FromResult((items, matching.Count));
    }

    public Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> FindCategoriesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        FindCategoriesCalls++;
        IReadOnlyDictionary<Guid, WholesaleCategoryRef> found = ids
            .Distinct()
            .Where(Categories.ContainsKey)
            .ToDictionary(id => id, id => new WholesaleCategoryRef(id, Categories[id].Name));
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<WholesaleCategoryNode>> ListCategoriesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WholesaleCategoryNode> nodes = Categories.Values.OrderBy(node => node.Name, StringComparer.Ordinal).ToList();
        return Task.FromResult(nodes);
    }

    public Task<(IReadOnlyList<WholesaleSampleVariant> Items, int TotalProducts)> SampleAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        int take,
        CancellationToken cancellationToken)
    {
        SampleCategoryIds.Add(categoryIds);
        SampleCalls.Add((currency, take));
        SampleExclusions.Add((excludedCategoryIds, excludedProductIds));
        var matching = Products
            .Where(product => product.IsActive && product.Prices.ContainsKey(currency))
            .Where(product => !IsExcluded(product, excludedCategoryIds, excludedProductIds))
            .Where(product => categoryIds is null
                || (product.CategoryId is { } category && categoryIds.Contains(category)))
            .OrderBy(product => product.Name, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<WholesaleSampleVariant> items = matching
            .Take(take)
            .Select(product => new WholesaleSampleVariant(product.VariantId, product.Code, product.Name, product.Prices[currency]))
            .ToList();
        return Task.FromResult((items, matching.Count));
    }
}

internal sealed class FakeUnitOfWork : IWholesaleUnitOfWork
{
    public int Saves { get; private set; }

    public int Discards { get; private set; }

    /// <summary>Lo que hace «soltar los cambios»: con EF, la próxima lectura trae la fila de la base.</summary>
    public Action? OnDiscard { get; set; }

    public void DiscardPendingChanges()
    {
        Discards++;
        OnDiscard?.Invoke();
    }

    /// <summary>Lo que lanza el próximo guardado (una carrera perdida en la base), una sola vez.</summary>
    public Exception? FailNextSaveWith { get; set; }

    /// <summary>Cuántos guardados pasan antes de que <see cref="FailNextSaveWith"/> lance: 1 deja pasar la marca de la
    /// aceptación y falla el commit final.</summary>
    public int FailAfterSaves { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (Saves >= FailAfterSaves && FailNextSaveWith is { } failure)
        {
            FailNextSaveWith = null;
            throw failure;
        }

        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed record AuditEntry(
    Guid TenantId,
    Guid ActorId,
    string Action,
    string ResourceType,
    string ResourceId,
    string Outcome,
    IReadOnlyCollection<string> ChangedFields,
    DateTimeOffset OccurredAt);

internal sealed class FakeAuditPublisher : IWholesaleAuditPublisher
{
    public List<AuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Entries.Add(new AuditEntry(tenantId, actorId, action, resourceType, resourceId, outcome, changedFields, occurredAt));
}

internal sealed class FakeTenantDefaultCurrency(string currency = "COP") : ITenantDefaultCurrency
{
    public Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(currency);
}

internal sealed class FakeTenantPriceRounding(PriceRounding rounding) : ITenantPriceRounding
{
    public List<string> AskedCurrencies { get; } = [];

    public Task<PriceRounding> GetAsync(Guid tenantId, string currency, CancellationToken cancellationToken)
    {
        AskedCurrencies.Add(currency);
        return Task.FromResult(rounding);
    }

    public Task<IReadOnlyDictionary<string, PriceRounding>> GetAllAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The wholesale preview asks for one currency.");
}
