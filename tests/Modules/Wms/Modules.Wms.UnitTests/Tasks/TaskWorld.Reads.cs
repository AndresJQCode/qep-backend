using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>Los puertos de lectura que usa el composer. Lo que ninguna prueba llama lanza.</summary>
internal sealed partial class TaskWorld
{
    private sealed class FakeClock(TaskWorld world) : IClock
    {
        public DateTimeOffset UtcNow => world.Now;
    }

    /// <summary>El huso de los tenants de prueba: Colombia, como el del arnés de integración.</summary>
    private sealed class FakeTenantClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Start, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class FakeWarehouses(TaskWorld world) : IWarehouseRepository
    {
        public Task<Warehouse?> GetAsync(Guid tenantId, WarehouseId id, CancellationToken ct)
        {
            world.Log.Add("warehouse-tree");
            return Task.FromResult(tenantId == Tenant && id == world.Warehouse.Id ? world.Warehouse : null);
        }

        public Task<IReadOnlyList<Warehouse>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken ct)
        {
            world.Log.Add("warehouse-tree");
            return Task.FromResult<IReadOnlyList<Warehouse>>(tenantId == Tenant ? [world.Warehouse] : []);
        }

        public Task<IReadOnlyDictionary<LocationId, string>> FindLocationCodesAsync(
            Guid tenantId, IReadOnlyCollection<LocationId> ids, CancellationToken ct)
        {
            world.LocationReads.Add(ids);
            return Task.FromResult<IReadOnlyDictionary<LocationId, string>>(
                tenantId == Tenant
                    ? world.Warehouse.Locations.Where(location => ids.Contains(location.Id)).ToDictionary(l => l.Id, l => l.Code)
                    : new Dictionary<LocationId, string>());
        }

        public void Add(Warehouse warehouse) => throw new NotSupportedException();

        public Task<Warehouse?> FindPrincipalAsync(Guid tenantId, CancellationToken ct) => throw new NotSupportedException();

        public Task<Location?> GetLocationAsync(Guid tenantId, LocationId id, CancellationToken ct) => throw new NotSupportedException();

        public Task<Location?> GetVirtualLocationAsync(Guid tenantId, WarehouseId warehouseId, LocationRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public void RemoveLocation(Location location) => throw new NotSupportedException();

        public Task<Location?> FindLocationByCodeAsync(Guid tenantId, WarehouseId warehouseId, string code, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCatalog : IWmsCatalogReader
    {
        public Task<IReadOnlyList<WmsVariantSnapshot>> GetVariantsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WmsVariantSnapshot>>(
                variantIds.Contains(Variant)
                    ? [new WmsVariantSnapshot(Variant, Variant, "Café", "Única", "V1", null, [12], true)]
                    : []);

        public Task<IReadOnlyList<WmsVariantSnapshot>> FindVariantsByCodesAsync(
            Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLots(TaskWorld world) : ILotRepository
    {
        public Task<IReadOnlyDictionary<Guid, Lot>> GetManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Lot>>(
                world.Lots.Where(lot => ids.Contains(lot.Id.Value)).ToDictionary(lot => lot.Id.Value));

        public Task<Lot?> FindAsync(Guid tenantId, Guid ownerId, Guid variantId, string code, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Add(Lot lot) => world.Lots.Add(lot);
    }

    private sealed class FakeProfiles : IVariantProfileRepository
    {
        public Task<IReadOnlyList<VariantLogisticsProfile>> ListAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VariantLogisticsProfile>>(
                variantIds.Contains(Variant) ? [VariantLogisticsProfile.CreateDefault(Tenant, Variant, Variant, [12], 0, Start)] : []);

        public void Add(VariantLogisticsProfile profile) => throw new NotSupportedException();

        public Task<VariantLogisticsProfile?> FindAsync(Guid tenantId, Guid variantId, CancellationToken ct) =>
            Task.FromResult<VariantLogisticsProfile?>(
                variantId == Variant ? VariantLogisticsProfile.CreateDefault(Tenant, Variant, Variant, [12], 0, Start) : null);

        public Task<VariantLogisticsProfile> GetOrCreateDefaultAsync(Guid tenantId, Guid variantId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMembers(TaskWorld world) : IWmsMemberDirectory
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                world.Names.Where(pair => memberIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

        public Task<bool> CanPerformAsync(Guid tenantId, Guid memberId, string permission, CancellationToken ct) =>
            Task.FromResult(world.Eligible.Contains(memberId));

        public Task<IReadOnlyList<Guid>> ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken ct)
        {
            world.PermissionLookups++;
            return Task.FromResult<IReadOnlyList<Guid>>(
                [.. world.Eligible.Where(member => permission != WmsPermissions.DocumentRead || !world.WithoutDocumentRead.Contains(member))]);
        }
    }

    public List<Lot> Lots { get; } = [];
}
