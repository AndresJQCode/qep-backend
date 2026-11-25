using BuildingBlocks.Application;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// Mayorista fase 3, adenda del contrato: la lista y el detalle de proveedores dicen en qué estado está la relación con el
/// mayorista vinculado (<c>Active</c>, <c>Blocked</c>, <c>Invited</c>), y <c>null</c> para un proveedor externo. Con
/// <c>Blocked</c> la pantalla no pide el catálogo.
/// </summary>
public sealed class LinkedRelationshipStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Wholesaler = Guid.CreateVersion7();
    private static readonly Guid OtherWholesaler = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SupplierWriteData Data(string number) => new(
        "Proveedor " + number, null, "NIT", number, "US", null, "Miami", null, null, null, 0, null, null, [], []);

    private readonly FakeRepository _repository = new();
    private readonly FakeRelationships _relationships = new();

    [Fact]
    public async Task TheListCarriesTheRelationshipStatusOfEachLinkedSupplier()
    {
        var blocked = Supplier.CreateLinked(Tenant, Wholesaler, Data("1"), new NoPhones(), Now);
        var orphan = Supplier.CreateLinked(Tenant, OtherWholesaler, Data("2"), new NoPhones(), Now);
        var external = Supplier.Create(Tenant, Data("3"), new NoPhones(), Now);
        _repository.Stored.AddRange([blocked, orphan, external]);
        _relationships.Statuses[Wholesaler] = "Blocked";

        var page = await new ListSuppliersHandler(_repository, new NoGeography(), new Context(), _relationships)
            .HandleAsync(new ListSuppliersQuery(Tenant, null, null, null, 1, 50), Ct);

        Assert.Equal(
            [("Blocked", true), (null, true), (null, false)],
            page.Items.Select(item => (item.LinkedRelationshipStatus, item.IsLinked)));
        // Una sola consulta por página, con los mayoristas de la página.
        var asked = Assert.Single(_relationships.Asked);
        Assert.Equal(Tenant, asked.Tenant);
        Assert.Equal(new[] { Wholesaler, OtherWholesaler }.Order(), asked.Wholesalers.Order());
    }

    [Fact]
    public async Task APageWithoutLinkedSuppliersDoesNotAsk()
    {
        _repository.Stored.Add(Supplier.Create(Tenant, Data("3"), new NoPhones(), Now));

        await new ListSuppliersHandler(_repository, new NoGeography(), new Context(), _relationships)
            .HandleAsync(new ListSuppliersQuery(Tenant, null, null, null, 1, 50), Ct);

        Assert.Empty(_relationships.Asked);
    }

    [Fact]
    public async Task TheDetailCarriesTheRelationshipStatus()
    {
        var linked = Supplier.CreateLinked(Tenant, Wholesaler, Data("1"), new NoPhones(), Now);
        _repository.Stored.Add(linked);
        _relationships.Statuses[Wholesaler] = "Active";

        var detail = await new GetSupplierHandler(_repository, new NoGeography(), new Context(), new Directory(), _relationships)
            .HandleAsync(new GetSupplierQuery(Tenant, linked.Id.Value), Ct);

        Assert.Equal("Active", detail.LinkedRelationshipStatus);
        Assert.Equal(new SupplierLinkedTenantDto(Wholesaler, "Mayorista Central"), detail.LinkedTenant);
    }

    [Fact]
    public async Task AnExternalSupplierHasNoRelationshipStatus()
    {
        var external = Supplier.Create(Tenant, Data("3"), new NoPhones(), Now);
        _repository.Stored.Add(external);

        var detail = await new GetSupplierHandler(_repository, new NoGeography(), new Context(), new Directory(), _relationships)
            .HandleAsync(new GetSupplierQuery(Tenant, external.Id.Value), Ct);

        Assert.Null(detail.LinkedRelationshipStatus);
        Assert.Empty(_relationships.Asked);
    }

    private sealed class FakeRelationships : ISupplierRelationshipLookup
    {
        public Dictionary<Guid, string> Statuses { get; } = [];

        public List<(Guid Tenant, IReadOnlyCollection<Guid> Wholesalers)> Asked { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> FindStatusesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> wholesalerTenantIds, CancellationToken cancellationToken)
        {
            Asked.Add((tenantId, wholesalerTenantIds));
            IReadOnlyDictionary<Guid, string> found = wholesalerTenantIds
                .Where(Statuses.ContainsKey)
                .ToDictionary(id => id, id => Statuses[id]);
            return Task.FromResult(found);
        }
    }

    private sealed class FakeRepository : ISupplierRepository
    {
        public List<Supplier> Stored { get; } = [];

        public Task<Supplier?> GetAsync(Guid tenantId, SupplierId supplierId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.SingleOrDefault(supplier => supplier.TenantId == tenantId && supplier.Id == supplierId));

        public Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
            Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int page, int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<Supplier>, int)>((Stored, Stored.Count));

        public void Add(Supplier supplier) => throw new NotSupportedException();

        public Task<Supplier?> FindByLinkedTenantAsync(Guid tenantId, Guid linkedTenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Supplier?> FindByIdentificationAsync(
            Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListCategoriesAsync(Guid tenantId, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Supplier>> ListForExportAsync(
            Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int skip, int take,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Directory : ITenantDirectory
    {
        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("Mayorista Central");

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Context : IExecutionContext
    {
        public Guid SubjectId => Guid.Parse("01900000-0000-7000-8000-0000000000aa");

        public TenantId TenantId => new(Tenant);

        public bool HasPermission(string permission) => true;
    }

    private sealed class NoGeography : ISupplierGeographyLookup
    {
        public Task<SupplierCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SupplierRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SupplierRegionRef>>(new Dictionary<Guid, SupplierRegionRef>());

        public Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SupplierCityRef>>(new Dictionary<Guid, SupplierCityRef>());
    }

    private sealed class NoPhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) => null;
    }
}
