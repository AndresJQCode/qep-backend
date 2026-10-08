using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Dobles de la consola de operador (spec 2026-10-08). A mano, como el resto del repo: lo que la
// prueba no usa lanza, para que no pase por un camino que no creía estar ejerciendo.

internal sealed class FixedTenantDirectory(TenantStatus? status) : ITenantDirectory
{
    public List<TenantId> Asked { get; } = [];

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(status);
    }

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FixedOperatorTenant(Guid? operatorTenantId) : IOperatorTenant
{
    public bool IsOperator(Guid tenantId) => operatorTenantId == tenantId;
}

internal sealed class OperatorContext(TenantId tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();
    public TenantId TenantId => tenantId;
    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>El lector con respuestas fijas. Anota cada pregunta: las pruebas de doble capa exigen que un
/// no operador nunca llegue a buscar el destino.</summary>
internal sealed class FixedOperatorTenantReader : IOperatorTenantReader
{
    public OperatorTenantListing Listing { get; set; } = new([], 0, 0, 0, 0);
    public Dictionary<TenantId, OperatorTenantSnapshot> Snapshots { get; } = [];
    public List<string> Asked { get; } = [];

    public Task<OperatorTenantListing> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        Asked.Add($"list:{search}:{page}:{pageSize}");
        return Task.FromResult(Listing);
    }

    public Task<OperatorTenantSnapshot?> FindAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Asked.Add($"find:{tenantId}");
        return Task.FromResult(Snapshots.GetValueOrDefault(tenantId));
    }

    public TenantChangeBatchPage History { get; set; } = new([], 0);

    public Task<TenantChangeBatchPage> ListHistoryAsync(
        TenantId tenantId, TenantModuleKey? moduleKey, int page, int pageSize, CancellationToken cancellationToken)
    {
        Asked.Add($"history:{moduleKey?.Value}:{page}:{pageSize}");
        return Task.FromResult(History);
    }
}

internal static class OperatorSnapshots
{
    public static OperatorTenantSnapshot Of(
        TenantId tenantId, IEnumerable<TenantModuleState> modules, TenantStatus status = TenantStatus.Active,
        IReadOnlyDictionary<TenantModuleKey, ChangeReason>? lastReasons = null, TenantStatusChange? lastStatus = null) =>
        new(tenantId.Value, "origen-botanico", "Origen Botánico", DateTimeOffset.UnixEpoch, status, 1,
            modules.ToArray(), lastReasons ?? new Dictionary<TenantModuleKey, ChangeReason>(), lastStatus);

    public static IEnumerable<TenantModuleState> Signup() =>
        TenantModuleKeys.DefaultForNewTenants.Select(key =>
            new TenantModuleState(key, TenantModuleStatus.Active, DateTimeOffset.UnixEpoch, TenantModuleSources.Signup));
}

// Los Steps* anotan en una lista compartida el orden de candado, lecturas, save y commit: la consola
// tiene que tomar el candado antes de leer (spec 2026-10-08 §3).

internal sealed class StepsTenantRepository(List<string> steps, params Tenant[] tenants) : ITenantRepository
{
    public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken)
    {
        steps.Add("read-tenant");
        return Task.FromResult(tenants.SingleOrDefault(tenant => tenant.Id == id));
    }

    public void Add(Tenant tenant) => throw new NotSupportedException();
}

internal sealed class StepsTenantModuleRepository(List<string> steps, params TenantModule[] rows) : ITenantModuleRepository
{
    public List<TenantModule> Rows { get; } = [.. rows];
    public List<TenantModule> Added { get; } = [];

    public void Add(TenantModule tenantModule) => Added.Add(tenantModule);

    public Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        steps.Add("read-modules");
        return Task.FromResult<IReadOnlyList<TenantModule>>(Rows.Where(row => row.TenantId == tenantId).ToList());
    }
}

internal sealed class RecordingTenantChangeRepository : ITenantChangeRepository
{
    public List<TenantChange> Added { get; } = [];
    public void Add(TenantChange change) => Added.Add(change);
}

/// <summary>Anota el candado, el SaveChanges y el commit en el orden en que pasan.</summary>
internal sealed class StepsUnitOfWork(List<string> steps) : ITenancyUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        steps.Add("save");
        return Task.FromResult(1);
    }

    public Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ITenantChangeScope> BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        steps.Add($"lock:{tenantId}");
        return Task.FromResult<ITenantChangeScope>(new Scope(steps));
    }

    private sealed class Scope(List<string> steps) : ITenantChangeScope
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            steps.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
