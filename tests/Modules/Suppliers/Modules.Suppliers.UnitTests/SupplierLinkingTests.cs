using BuildingBlocks.Application;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// Spec 2026-11-17 (mayorista), D-3 y D-8: al aceptar una invitación, el mayorista queda como proveedor vinculado en el tenant
/// del distribuidor. Paso idempotente: por vínculo, por documento o creándolo, en ese orden.
/// </summary>
public sealed class SupplierLinkingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Wholesaler = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Bogota = Guid.CreateVersion7();

    private static SupplierWriteData Data(Guid? cityId = null, string number = "900123456-7") => new(
        "Mayorista Origen",
        "Origen Botánico SAS",
        "NIT",
        number,
        "CO",
        cityId ?? Bogota,
        null,
        "3001234567",
        "ventas@origen.co",
        "Calle 1 # 2-3",
        0,
        null,
        null,
        [],
        []);

    private sealed class Fixture
    {
        public FakeSupplierRepository Repository { get; } = new();
        public FakeGeography Geography { get; } = new(Bogota);
        public RecordingAudit Audit { get; } = new();
        public CountingUnitOfWork UnitOfWork { get; } = new();

        public SupplierLinking Linking() =>
            new(Repository, Geography, UnitOfWork, Audit, new FixedClock(Now), new NoPhones());

        public Task<Guid> EnsureAsync(SupplierWriteData? data = null) =>
            Linking().EnsureLinkedAsync(Tenant, Wholesaler, Actor, data ?? Data(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnAlreadyLinkedSupplierIsReturnedUntouched()
    {
        var fixture = new Fixture();
        var linked = Supplier.CreateLinked(Tenant, Wholesaler, Data(number: "111"), new NoPhones(), Now.AddDays(-1));
        fixture.Repository.Stored.Add(linked);

        var id = await fixture.EnsureAsync();

        Assert.Equal(linked.Id.Value, id);
        Assert.Equal((1, "111"), (linked.Version, linked.Identification.Number));
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
        Assert.Empty(fixture.Repository.Added);
    }

    [Fact]
    public async Task AnUnlinkedSupplierWithTheSameDocumentGetsLinked()
    {
        var fixture = new Fixture();
        var existing = Supplier.Create(Tenant, Data(number: " 900123456-7"), new NoPhones(), Now.AddDays(-1));
        fixture.Repository.Stored.Add(existing);

        var id = await fixture.EnsureAsync();

        Assert.Equal(existing.Id.Value, id);
        Assert.Equal((Wholesaler, 2), (existing.LinkedTenantId, existing.Version));
        Assert.Empty(fixture.Repository.Added);
        Assert.Equal(1, fixture.UnitOfWork.Saves);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((Tenant, Actor, "suppliers.supplier.linked", existing.Id.ToString()),
            (audit.TenantId, audit.ActorId, audit.Action, audit.ResourceId));
        Assert.Equal("suppliers.supplier.linked", SupplierAuditActions.Linked);
    }

    [Fact]
    public async Task WithoutMatchesALinkedSupplierIsCreated()
    {
        var fixture = new Fixture();
        // Otro tenant con el mismo documento no cuenta: cada búsqueda va con el tenantId.
        fixture.Repository.Stored.Add(Supplier.Create(Guid.CreateVersion7(), Data(), new NoPhones(), Now));

        var id = await fixture.EnsureAsync();

        var created = Assert.Single(fixture.Repository.Added);
        Assert.Equal((created.Id.Value, Tenant, Wholesaler, "Mayorista Origen", "900123456-7"),
            (id, created.TenantId, created.LinkedTenantId, created.Name, created.Identification.Number));
        Assert.Equal([Bogota], fixture.Geography.Asked);
        Assert.Equal(1, fixture.UnitOfWork.Saves);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((Tenant, Actor, SupplierAuditActions.Linked), (audit.TenantId, audit.ActorId, audit.Action));
    }

    [Fact]
    public async Task AColombianCityThatDoesNotExistIsRejected()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<SuppliersDomainException>(() => fixture.EnsureAsync(Data(cityId: Guid.CreateVersion7())));

        Assert.Equal("suppliers.supplier.city_not_found", error.Code);
        Assert.Empty(fixture.Repository.Added);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
    }

    // Dos pestañas: la otra creó el proveedor vinculado entre la búsqueda y el commit. El índice choca, se descarta lo
    // pendiente y se relee una vez desde el paso 1.
    [Fact]
    public async Task ACollisionAtCommitRereadsOnceAndReturnsTheWinner()
    {
        var fixture = new Fixture();
        var winner = Supplier.CreateLinked(Tenant, Wholesaler, Data(), new NoPhones(), Now);
        fixture.UnitOfWork.OnSave = attempt =>
        {
            if (attempt == 1)
            {
                fixture.Repository.Stored.Add(winner);
                throw new SuppliersDomainException("suppliers.supplier.identification_taken", "taken");
            }
        };

        var id = await fixture.EnsureAsync();

        Assert.Equal(winner.Id.Value, id);
        Assert.Equal(1, fixture.UnitOfWork.Saves);
        Assert.Equal(1, fixture.UnitOfWork.Discards);
    }

    // Dos pestañas vinculan el mismo proveedor por documento: la segunda pierde por la versión. Releer la encuentra ya
    // vinculada a este mayorista y termina igual.
    [Fact]
    public async Task AConcurrentLinkOfTheSameSupplierRereadsOnce()
    {
        var fixture = new Fixture();
        var existing = Supplier.Create(Tenant, Data(), new NoPhones(), Now.AddDays(-1));
        fixture.Repository.Stored.Add(existing);
        fixture.UnitOfWork.OnSave = attempt =>
        {
            if (attempt == 1)
            {
                throw new RequestConcurrencyException("concurrency.conflict", "changed");
            }
        };

        var id = await fixture.EnsureAsync();

        Assert.Equal(existing.Id.Value, id);
        Assert.Equal((1, 1), (fixture.UnitOfWork.Saves, fixture.UnitOfWork.Discards));
    }

    [Fact]
    public async Task ASecondCollisionIsNotRetried()
    {
        var fixture = new Fixture();
        fixture.UnitOfWork.OnSave = _ =>
            throw new ResourceConflictException("suppliers.supplier.linked_elsewhere", "linked");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => fixture.EnsureAsync());

        Assert.Equal("suppliers.supplier.linked_elsewhere", error.Code);
        Assert.Equal(2, fixture.UnitOfWork.Saves);
    }

    private sealed class FakeSupplierRepository : ISupplierRepository
    {
        public List<Supplier> Stored { get; } = [];

        public List<Supplier> Added { get; } = [];

        public Task<Supplier?> FindByLinkedTenantAsync(Guid tenantId, Guid linkedTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.SingleOrDefault(supplier =>
                supplier.TenantId == tenantId && supplier.LinkedTenantId == linkedTenantId));

        public Task<Supplier?> FindByIdentificationAsync(
            Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.SingleOrDefault(supplier =>
                supplier.TenantId == tenantId &&
                supplier.Identification.Type == type &&
                supplier.Identification.Number == number));

        public void Add(Supplier supplier) => Added.Add(supplier);

        public Task<Supplier?> GetAsync(Guid tenantId, SupplierId supplierId, CancellationToken cancellationToken) =>
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

    private sealed class FakeGeography(params Guid[] known) : ISupplierGeographyLookup
    {
        public List<Guid> Asked { get; } = [];

        public Task<SupplierCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken)
        {
            Asked.Add(cityId);
            return Task.FromResult(known.Contains(cityId)
                ? new SupplierCityRef(cityId, "11001", "Bogotá", Guid.CreateVersion7(), "11", "Bogotá D.C.")
                : null);
        }

        public Task<IReadOnlyDictionary<Guid, SupplierRegionRef>> FindRegionsAsync(
            IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SupplierRegionRef>>(new Dictionary<Guid, SupplierRegionRef>());

        public Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingAudit : ISuppliersAuditPublisher
    {
        public List<(Guid TenantId, Guid ActorId, string Action, string ResourceId)> Entries { get; } = [];

        public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Entries.Add((tenantId, actorId, action, resourceId));
    }

    /// <summary>Cuenta los commits y los descartes; <see cref="OnSave"/> recibe el número de intento y puede lanzar.</summary>
    private sealed class CountingUnitOfWork : ISuppliersUnitOfWork
    {
        public int Saves { get; private set; }

        public int Discards { get; private set; }

        public Action<int>? OnSave { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            OnSave?.Invoke(Saves);
            return Task.CompletedTask;
        }

        public void DiscardPendingChanges() => Discards++;
    }

    private sealed class NoPhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
