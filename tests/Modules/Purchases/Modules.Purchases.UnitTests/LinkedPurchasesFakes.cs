using BuildingBlocks.Application;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.UnitTests;

internal sealed class FakePurchasesClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Cualquier sujeto tiene la membresía <see cref="PurchaseOrderTestData.MemberId"/>.</summary>
internal sealed class FakeMembership : IMembershipDirectory
{
    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>([]);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(PurchaseOrderTestData.MemberId);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([PurchaseOrderTestData.MemberId]);
}

/// <summary>Las órdenes en memoria, acotadas al tenant como el repositorio real.</summary>
internal sealed class FakePurchaseOrderRepository : IPurchaseOrderRepository
{
    public List<PurchaseOrder> Orders { get; } = [];

    public PurchaseOrder Seed(PurchaseOrder order)
    {
        Orders.Add(order);
        return order;
    }

    public Task<PurchaseOrder?> GetAsync(Guid tenantId, PurchaseOrderId id, CancellationToken cancellationToken) =>
        Task.FromResult(Orders.FirstOrDefault(order => order.TenantId == tenantId && order.Id == id));

    public void Add(PurchaseOrder order) => Orders.Add(order);

    public void Remove(PurchaseOrder order) => Orders.Remove(order);

    public Task<(IReadOnlyList<PurchaseOrder> Items, int Total)> SearchAsync(
        Guid tenantId,
        PurchaseOrderStatus? status,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<PurchaseOrderStatus, int>> CountByStatusAsync(
        Guid tenantId,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakePurchaseOrderNumbers : IPurchaseOrderNumberGenerator
{
    private long _next;

    public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(++_next);
}

internal sealed class FakePurchasesUnitOfWork : IPurchasesUnitOfWork
{
    public int Saves { get; private set; }

    public int Transactions { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }

    public Task<IPurchasesTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        Transactions++;
        return Task.FromResult<IPurchasesTransaction>(new FakeTransaction());
    }

    private sealed class FakeTransaction : IPurchasesTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record PurchasesAuditEntry(Guid TenantId, Guid ActorId, string Action, IReadOnlyCollection<string> ChangedFields);

internal sealed class FakePurchasesAudit : IPurchasesAuditPublisher
{
    public List<PurchasesAuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Entries.Add(new PurchasesAuditEntry(tenantId, actorId, action, changedFields));
}

/// <summary>
/// El catálogo del mayorista visto por el comprador. Una variante que no está es la que el adaptador real rechaza con
/// <c>wholesale.product.not_visible</c>; con <see cref="Forbidden"/> la relación no está activa (403).
/// </summary>
internal sealed class FakeWholesaleCatalog : IWholesaleCatalog
{
    public string Currency { get; set; } = "USD";

    public Dictionary<Guid, WholesaleCatalogItem> Items { get; } = [];

    public bool Forbidden { get; set; }

    public List<(Guid BuyerTenantId, Guid WholesalerTenantId, string? Search, int Page, int PageSize)> Searches { get; } = [];

    public WholesaleCatalogItem Add(string code, decimal basePrice, decimal discountPercent, decimal listPrice, decimal taxRatePercent)
    {
        var item = new WholesaleCatalogItem(Guid.CreateVersion7(), code, "Producto " + code, basePrice, discountPercent, listPrice, taxRatePercent);
        Items[item.VariantId] = item;
        return item;
    }

    /// <summary>Spec 2026-11-20: el árbol que el mayorista le deja ver al comprador, en el orden que lo da.</summary>
    public List<WholesaleCategory> Categories { get; } = [];

    /// <summary>El <c>categoryId</c> de cada búsqueda, en el mismo orden que <see cref="Searches"/>.</summary>
    public List<Guid?> SearchCategoryIds { get; } = [];

    public Task<IReadOnlyList<WholesaleCategory>> ListCategoriesAsync(
        Guid buyerTenantId, Guid wholesalerTenantId, CancellationToken cancellationToken)
    {
        EnsureAllowed();
        return Task.FromResult<IReadOnlyList<WholesaleCategory>>(Categories.ToList());
    }

    public Task<WholesaleCatalogPage> SearchAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        string? search,
        Guid? categoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        EnsureAllowed();
        Searches.Add((buyerTenantId, wholesalerTenantId, search, page, pageSize));
        SearchCategoryIds.Add(categoryId);
        return Task.FromResult(new WholesaleCatalogPage(Currency, "Distribuidor estándar", 15m, Items.Values.ToList(), Items.Count, ShowPrices));
    }

    public Task<(string Currency, IReadOnlyDictionary<Guid, WholesaleCatalogItem> Items, bool ShowPrices)> PriceAsync(
        Guid buyerTenantId, Guid wholesalerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        EnsureAllowed();
        var missing = variantIds.FirstOrDefault(id => !Items.ContainsKey(id));
        if (missing != Guid.Empty)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.ProductNotVisible, "Not visible.");
        }

        IReadOnlyDictionary<Guid, WholesaleCatalogItem> found = variantIds.Distinct().ToDictionary(id => id, id => Items[id]);
        return Task.FromResult((Currency, found, ShowPrices));
    }

    /// <summary>Spec 2026-11-19: el flag vivo de la lista del mayorista (por defecto se muestran).</summary>
    public bool ShowPrices { get; set; } = true;

    private void EnsureAllowed()
    {
        if (Forbidden)
        {
            throw new RequestForbiddenException(PurchasesErrorCodes.RelationshipInactive, "Inactive.");
        }
    }
}

/// <summary>El pedido del mayorista en memoria: anota cada envío y cada anulación.</summary>
internal sealed class FakeWholesaleOrderIntake : IWholesaleOrderIntake
{
    public List<WholesaleIntakeRequest> Submitted { get; } = [];

    public List<(Guid BuyerTenantId, Guid WholesalerTenantId, Guid PurchaseOrderId, string Number, Guid? OrderId)> Cancelled { get; } = [];

    /// <summary>Con valor, el próximo envío devuelve este resultado (un pedido que ya existía).</summary>
    public WholesaleIntakeResult? Existing { get; set; }

    public Exception? CancelFailure { get; set; }

    public Guid OrderId { get; } = Guid.CreateVersion7();

    /// <summary>Spec 2026-11-19: el flag vivo de la lista con que el intake crea el pedido (por defecto se muestran).</summary>
    public bool ShowPrices { get; set; } = true;

    public Task<WholesaleIntakeResult> SubmitAsync(WholesaleIntakeRequest request, CancellationToken cancellationToken)
    {
        Submitted.Add(request);
        if (Existing is { } existing)
        {
            return Task.FromResult(existing);
        }

        // El pedido nuevo conserva los ids de línea de la orden; el costo es el del precio de lista sin IVA (11900 @19 → 10000).
        var lines = request.Lines
            .Select(line => new LinkedLineSnapshot(
                line.PurchaseOrderLineId, line.VariantId, "SKU", "Producto", line.Quantity, 10000m, 19m, 12500m, 4.8m, 11900m))
            .ToList();
        return Task.FromResult(new WholesaleIntakeResult(OrderId, "PED-0042", "USD", lines, AlreadyExisted: false, ShowPrices));
    }

    public Task CancelAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber,
        Guid? orderId,
        CancellationToken cancellationToken)
    {
        if (CancelFailure is { } failure)
        {
            throw failure;
        }

        Cancelled.Add((buyerTenantId, wholesalerTenantId, purchaseOrderId, purchaseOrderNumber, orderId));
        return Task.CompletedTask;
    }
}

/// <summary>El catálogo del comprador para el aplicador: por código exacto, en cualquier estado (D-18).</summary>
internal sealed class FakePurchasesCatalogWriter : IPurchasesCatalogWriter
{
    public Dictionary<string, Guid> Existing { get; } = new(StringComparer.Ordinal);

    public List<(string Code, string Name, string Currency, decimal? UnitCost)> Calls { get; } = [];

    /// <summary>Los códigos cuyo alta falla (índice único, nombre largo…).</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    public Task<PurchasesCatalogEntry> EnsureVariantAsync(
        Guid tenantId, string code, string name, string currency, decimal? unitCost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add((code, name, currency, unitCost));
        if (Failing.Contains(code))
        {
            throw new InvalidOperationException($"Could not create '{code}'.");
        }

        if (Existing.TryGetValue(code, out var existing))
        {
            return Task.FromResult(new PurchasesCatalogEntry(existing, Created: false, ProductId: existing));
        }

        var created = Guid.CreateVersion7();
        Existing[code] = created;
        return Task.FromResult(new PurchasesCatalogEntry(created, Created: true, ProductId: created));
    }
}

/// <summary>El copiador de imágenes del mayorista: anota lo pedido, o falla si se le indica.</summary>
internal sealed class FakePurchasesProductImages : IPurchasesProductImages
{
    public List<PendingProductImage> Copies { get; } = [];

    public bool Fails { get; set; }

    public Task CopyAsync(PendingProductImage image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Copies.Add(image);
        return Fails ? throw new InvalidOperationException("Storage is down.") : Task.CompletedTask;
    }
}
