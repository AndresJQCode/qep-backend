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

internal sealed class FakePriceListRepository : IPriceListRepository
{
    public Dictionary<PriceListId, PriceList> Lists { get; } = [];

    public List<PriceList> Removed { get; } = [];

    /// <summary>Cualquier lectura o escritura: «no tocó el repositorio» es cero.</summary>
    public int Calls { get; private set; }

    public PriceList Seed(PriceList list)
    {
        Lists[list.Id] = list;
        return list;
    }

    public Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Lists.TryGetValue(id, out var list) && list.TenantId == tenantId ? list : null);
    }

    public Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        IReadOnlyList<PriceList> lists = Lists.Values.Where(list => list.TenantId == tenantId).ToList();
        return Task.FromResult(lists);
    }

    public Task<IReadOnlyDictionary<PriceListId, PriceList>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<PriceListId> ids, CancellationToken cancellationToken)
    {
        Calls++;
        FindManyCalls++;
        IReadOnlyDictionary<PriceListId, PriceList> found = ids
            .Distinct()
            .Where(id => Lists.TryGetValue(id, out var list) && list.TenantId == tenantId)
            .ToDictionary(id => id, id => Lists[id]);
        return Task.FromResult(found);
    }

    public int FindManyCalls { get; private set; }

    // El índice de la base compara lower(name): acá, sin importar mayúsculas.
    public Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Lists.Values.Any(list =>
            list.TenantId == tenantId
            && list.Id != except
            && string.Equals(list.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public void Add(PriceList list)
    {
        Calls++;
        Lists[list.Id] = list;
    }

    public void Remove(PriceList list)
    {
        Calls++;
        Lists.Remove(list.Id);
        Removed.Add(list);
    }
}

internal sealed class FakeDistributorUsage : IDistributorUsage
{
    public Dictionary<PriceListId, int> Counts { get; } = [];

    public Task<IReadOnlyDictionary<PriceListId, int>> CountByPriceListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<PriceListId, int>>(new Dictionary<PriceListId, int>(Counts));
}

/// <summary>Un producto con una sola variante: lo que el lector real ya resolvió (vendible, nombre visible, precios).</summary>
internal sealed record FakeCatalogProduct(
    Guid VariantId,
    string Code,
    string Name,
    Guid? CategoryId,
    IReadOnlyDictionary<string, decimal> Prices,
    bool IsActive = true);

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

    public List<(string Currency, int Take)> SampleCalls { get; } = [];

    public Guid AddCategory(string name, Guid? parentId = null)
    {
        var id = Guid.CreateVersion7();
        Categories[id] = new WholesaleCategoryNode(id, name, parentId);
        return id;
    }

    public void AddProduct(string code, string name, Guid? categoryId, decimal? cop, decimal? usd = null, bool isActive = true)
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

        Products.Add(new FakeCatalogProduct(Guid.CreateVersion7(), code, name, categoryId, prices, isActive));
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
        Guid tenantId, string currency, IReadOnlyCollection<Guid>? categoryIds, int take, CancellationToken cancellationToken)
    {
        SampleCategoryIds.Add(categoryIds);
        SampleCalls.Add((currency, take));
        var matching = Products
            .Where(product => product.IsActive && product.Prices.ContainsKey(currency))
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

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextSaveWith is { } failure)
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
