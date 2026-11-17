using Modules.Purchases.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Purchases.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeSupplierLookup : IPurchasesSupplierLookup
{
    public Dictionary<Guid, PurchasesSupplierRef> Suppliers { get; } = [];

    public int FindManyCalls { get; private set; }

    public PurchasesSupplierRef Add(Guid id, string name, bool isActive = true)
    {
        var supplier = new PurchasesSupplierRef(id, name, "NIT", "900123456", isActive);
        Suppliers[id] = supplier;
        return supplier;
    }

    public Task<PurchasesSupplierRef?> FindAsync(Guid tenantId, Guid supplierId, CancellationToken cancellationToken) =>
        Task.FromResult(Suppliers.GetValueOrDefault(supplierId));

    public Task<IReadOnlyDictionary<Guid, PurchasesSupplierRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        FindManyCalls++;
        IReadOnlyDictionary<Guid, PurchasesSupplierRef> found = supplierIds
            .Where(Suppliers.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => Suppliers[id]);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> SearchIdsAsync(Guid tenantId, string search, int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> ids = Suppliers.Values
            .Where(supplier => supplier.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(supplier => supplier.Id)
            .Take(take)
            .ToList();
        return Task.FromResult(ids);
    }
}

internal sealed class FakeCatalogLookup : IPurchasesCatalogLookup
{
    public Dictionary<Guid, PurchasesVariantRef> Variants { get; } = [];

    public PurchasesVariantRef Add(string code, string name, decimal taxRatePercent)
    {
        var variant = new PurchasesVariantRef(Guid.CreateVersion7(), code, name, taxRatePercent);
        Variants[variant.VariantId] = variant;
        return variant;
    }

    public Task<IReadOnlyDictionary<Guid, PurchasesVariantRef>> FindVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PurchasesVariantRef> found = variantIds
            .Where(Variants.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => Variants[id]);
        return Task.FromResult(found);
    }
}

internal sealed class FakeMemberDirectory : IPurchasesMemberDirectory
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, string> found = memberIds
            .Where(Names.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(found);
    }
}
