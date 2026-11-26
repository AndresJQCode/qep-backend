using BuildingBlocks.Application;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// Spec 2026-11-23: la región de un proveedor de afuera se resuelve contra Geography antes de guardarla. El validador ya
/// rechazó la región con Colombia; acá se comprueba que exista y sea del país del proveedor.
/// </summary>
public sealed class SupplierRegionHandlerTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid MadridRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid LisbonRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");

    private static CreateSupplierCommand ForeignCommand(string country, Guid? regionId) => new(
        TenantId,
        "Iberia Suministros S.L.",
        null,
        "NIT",
        "B12345678",
        country,
        null,
        "Madrid",
        null,
        null,
        null,
        30,
        null,
        null,
        [],
        [],
        regionId);

    private static CreateSupplierHandler Handler(FakeRepository repository) => new(
        repository,
        new FakeGeography(),
        new FakeUnitOfWork(),
        new FakeAudit(),
        new ManagerContext(),
        new FixedClock(),
        new CreateSupplierValidator(),
        new NoPhones());

    [Fact]
    public async Task RegionOfAnotherCountryIsRejected()
    {
        var repository = new FakeRepository();

        var failure = await Assert.ThrowsAsync<SuppliersDomainException>(() =>
            Handler(repository).HandleAsync(ForeignCommand("ES", LisbonRegionId), TestContext.Current.CancellationToken));

        Assert.Equal("suppliers.supplier.region_invalid", failure.Code);
        Assert.Empty(repository.Added);
    }

    [Fact]
    public async Task ARegionOfTheSupplierCountryIsStoredAndReturned()
    {
        var repository = new FakeRepository();

        var response = await Handler(repository).HandleAsync(
            ForeignCommand("ES", MadridRegionId), TestContext.Current.CancellationToken);

        Assert.Equal(MadridRegionId, Assert.Single(repository.Added).RegionId);
        Assert.Equal("Madrid", response.Region?.Name);
    }

    private sealed class FakeRepository : ISupplierRepository
    {
        public List<Supplier> Added { get; } = [];

        public void Add(Supplier supplier) => Added.Add(supplier);

        public Task<Supplier?> GetAsync(Guid tenantId, SupplierId supplierId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Supplier?> FindByLinkedTenantAsync(Guid tenantId, Guid linkedTenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Supplier?> FindByIdentificationAsync(
            Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
            Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int page, int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListCategoriesAsync(Guid tenantId, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Supplier>> ListForExportAsync(
            Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int skip, int take,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeGeography : ISupplierGeographyLookup
    {
        public Task<SupplierCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SupplierRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SupplierRegionRef>>(
                new[]
                {
                    new SupplierRegionRef(MadridRegionId, "ES", "Madrid"),
                    new SupplierRegionRef(LisbonRegionId, "PT", "Lisboa")
                }.Where(region => regionIds.Contains(region.Id)).ToDictionary(region => region.Id));
    }

    private sealed class FakeUnitOfWork : ISuppliersUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void DiscardPendingChanges()
        {
        }
    }

    private sealed class FakeAudit : ISuppliersAuditPublisher
    {
        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
        {
        }
    }

    private sealed class ManagerContext : IExecutionContext
    {
        public Guid SubjectId => Guid.Parse("01900000-0000-7000-8000-000000000002");

        public TenantId TenantId => new(SupplierRegionHandlerTests.TenantId);

        public bool HasPermission(string permission) => permission == SuppliersPermissions.Manage;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoPhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) => null;
    }
}
