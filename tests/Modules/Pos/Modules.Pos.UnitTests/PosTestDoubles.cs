using BuildingBlocks.Application;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pos.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class FakeTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, Bogota));
}

internal sealed class InMemoryCashSessionRepository : ICashSessionRepository
{
    public List<CashSession> Sessions { get; } = [];

    public Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session => session.TenantId == tenantId && session.Id == id));

    public Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session =>
            session.TenantId == tenantId && session.CashierId == cashier && session.Status == CashSessionStatus.Open));

    public void Add(CashSession session) => Sessions.Add(session);
}

/// <summary>
/// Sólo encuentra lo "commiteado" (Stored), como una consulta real: lo agregado sin guardar no
/// aparece en FindAsync. FakeUnitOfWork pasa Added a Stored al guardar y lo descarta en Reset.
/// </summary>
internal sealed class InMemoryPosSaleRepository(InMemoryCashSessionRepository sessions) : IPosSaleRepository
{
    public List<PosSale> Stored { get; } = [];

    public List<PosSale> Added { get; } = [];

    public InMemoryCashSessionRepository Sessions { get; } = sessions;

    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.SingleOrDefault(sale => sale.TenantId == tenantId && sale.Id == id));

    public void Add(PosSale sale) => Added.Add(sale);

    public void Commit()
    {
        Stored.AddRange(Added);
        Added.Clear();
    }

    public void Discard() => Added.Clear();
}

internal sealed class FakeUnitOfWork(InMemoryPosSaleRepository sales) : IPosUnitOfWork
{
    public int SaveCalls { get; private set; }

    public int ResetCalls { get; private set; }

    public int Commits { get; private set; }

    /// <summary>Cada SaveChanges saca un gancho; si devuelve una excepción, la lanza.</summary>
    public Queue<Func<Exception?>> OnSave { get; } = new();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCalls++;
        if (OnSave.TryDequeue(out var hook) && hook() is { } exception)
        {
            throw exception;
        }

        sales.Commit();
        return Task.FromResult(1);
    }

    public Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IPosTransaction>(new FakeTransaction(this));

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCalls++;
        sales.Discard();
        return Task.CompletedTask;
    }

    private sealed class FakeTransaction(FakeUnitOfWork owner) : IPosTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeNumberGenerator : IPosSaleNumberGenerator
{
    private long _next = 1;

    public int Calls { get; private set; }

    public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_next++);
    }
}

internal sealed record AuditEntry(
    Guid TenantId, Guid ActorId, string Action, string ResourceType, string ResourceId,
    IReadOnlyCollection<string> ChangedFields);

internal sealed class RecordingAuditPublisher : IPosAuditPublisher
{
    public List<AuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceType, string resourceId,
        string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt) =>
        Entries.Add(new AuditEntry(tenantId, actorId, action, resourceType, resourceId, changedFields));
}

internal sealed class FakeCompanyLookup : IPosCompanyLookup
{
    public List<(Guid TenantId, PosCompanyRef Company)> Companies { get; } = [];

    public Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PosCompanyRef>>(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.IsActive)
            .Select(entry => entry.Company)
            .ToList());

    public Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.Id == companyId)
            .Select(entry => entry.Company)
            .SingleOrDefault());
}

internal sealed class FakeCashierLookup : IPosCashierLookup
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult(Names.TryGetValue(membershipId, out var name) ? name : null);
}

/// <summary>Todo lo que un handler del POS necesita, con el cajero del ejemplo ya miembro activo.</summary>
internal sealed class PosTestBed
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");

    public PosTestBed()
    {
        Sales = new InMemoryPosSaleRepository(Sessions);
        UnitOfWork = new FakeUnitOfWork(Sales);
        Memberships.Active[(UserId, PosFixtures.TenantId)] = PosFixtures.Cashier.Value;
        Cashiers.Names[PosFixtures.Cashier.Value] = "Laura Gómez";
    }

    public FakeMembershipDirectory Memberships { get; } = new();

    public InMemoryCashSessionRepository Sessions { get; } = new();

    public InMemoryPosSaleRepository Sales { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeNumberGenerator Numbers { get; } = new();

    public RecordingAuditPublisher Audit { get; } = new();

    public FakeCompanyLookup Companies { get; } = new();

    public FakeCashierLookup Cashiers { get; } = new();

    public FakeClock Clock { get; } = new(PosFixtures.Now);

    public FakeTenantClock TenantClock { get; } = new(PosFixtures.Now);

    /// <summary>El usuario autenticado de las pruebas; instancia para que Context no sea estatico (CA1822).</summary>
    public Guid SubjectId { get; } = UserId;

    public FakeExecutionContext Context(params string[] permissions) =>
        new(PosFixtures.TenantId, SubjectId, permissions);

    public CashSession OpenSessionInStore(decimal openingFloat = 100_000m)
    {
        var session = PosFixtures.OpenSession(openingFloat);
        Sessions.Add(session);
        return session;
    }

    public PosCompanyRef AddCompany(string name = "Origen Botánico SAS", bool active = true, Guid? tenantId = null)
    {
        var company = new PosCompanyRef(Guid.CreateVersion7(), name, "900123456-1", "Cra 50 # 10-20", "6045551234", active);
        Companies.Companies.Add((tenantId ?? PosFixtures.TenantId, company));
        return company;
    }
}
