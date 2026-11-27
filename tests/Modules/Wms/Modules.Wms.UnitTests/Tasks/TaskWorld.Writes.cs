using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>Quién llama, la persistencia de tareas, la unidad de trabajo y la auditoría, en memoria.</summary>
internal sealed partial class TaskWorld
{
    /// <summary>La membresía de quien llama.</summary>
    public Guid CallerMember { get; set; } = Member;

    public HashSet<string> Permissions { get; } =
        [WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform, WmsPermissions.TaskManage];

    /// <summary>Las membresías que pueden hacer una tarea.</summary>
    public HashSet<Guid> Eligible { get; } = [Member];

    /// <summary>Las membresías que pueden hacer una tarea pero ya no tienen <c>wms.document.read</c>, que exige toda ruta de tareas.
    /// </summary>
    public HashSet<Guid> WithoutDocumentRead { get; } = [];

    /// <summary>Cuántas veces se pidió la lista de elegibles de un permiso.</summary>
    public int PermissionLookups { get; set; }

    public List<(string Action, IReadOnlyDictionary<string, string?> Fields)> Audit { get; } = [];

    /// <summary>La que devuelve «la mejor candidata».</summary>
    public Guid? Candidate { get; set; }

    /// <summary>Lo que pasa con una tarea justo antes de que el repositorio la entregue al cargarla: el barrido o un supervisor que
    /// actúan entre la lectura y el bloqueo de quien la reclama.</summary>
    public Action<WarehouseTask>? OnLoadTask { get; set; }

    /// <summary>Los ganchos que corren cuando una tarea se cierra, en orden de registro.</summary>
    public List<ITaskClosedHook> Hooks { get; } = [];

    public TaskClosedNotifier Notifier() => new(Hooks);

    public OperatorLineRunner LineRunner(string? key = "key-1") =>
        new(
            Context(),
            Authorization,
            Caller(),
            UnitOfWork(),
            new IdempotencyScope { Key = key },
            ReceiptStore(),
            Repository(),
            new OperatorTaskGuard(Context(), Authorization),
            Metrics,
            Clock(),
            Composer(),
            Notifier(),
            IncidentRecorder());

    /// <summary>Las novedades que se agregaron, en orden. El doble no anota nada en <see cref="Log"/>: el orden que importa en las
    /// pruebas es el de los bloqueos, el ledger, el recibo y el guardado.</summary>
    public List<Incident> Incidents { get; } = [];

    public TaskIncidentRecorder IncidentRecorder() => new(new FakeIncidents(this));

    public SupervisorTaskRunner SupervisorRunner() =>
        new(Context(), Authorization, Repository(), UnitOfWork(), Recorder(), Clock(), Caller(), Composer(), Notifier());

    public IExecutionContext Context() => new FakeContext(this);

    public WmsAuthorization Authorization { get; } = new(new FakeModules());

    public WmsCaller Caller() => new(new FakeMemberships(this), Context());

    public IWarehouseTaskRepository Repository() => new FakeTasks(this);

    public IWmsUnitOfWork UnitOfWork() => new FakeUnitOfWork(this);

    public IWmsMemberDirectory Members() => new FakeMembers(this);

    public IWarehouseRepository Warehouses() => new FakeWarehouses(this);

    public IClock Clock() => new FakeClock(this);

    public IWmsAuditRecorder Recorder() => new FakeAudit(this);

    private sealed class FakeContext(TaskWorld world) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");

        public TenantId TenantId => new(Tenant);

        public bool HasPermission(string permission) => world.Permissions.Contains(permission);
    }

    private sealed class FakeModules : ITenantModules
    {
        // null: un tenant sin fila, que el guard deja pasar.
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FakeMemberships(TaskWorld world) : IMembershipDirectory
    {
        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(world.CallerMember);

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
            Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAudit(TaskWorld world) : IWmsAuditRecorder
    {
        public void Record(
            Guid tenantId,
            Guid actorId,
            WmsActorType actorType,
            string action,
            string resourceType,
            string resourceId,
            IReadOnlyDictionary<string, string?> changedFields,
            DateTimeOffset occurredAt) => world.Audit.Add((action, changedFields));
    }

    private sealed class FakeUnitOfWork(TaskWorld world) : IWmsUnitOfWork
    {
        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            world.Log.Add("begin");
            return Task.FromResult<IAsyncDisposable>(new Transaction());
        }

        public Task LockAsync(string key, CancellationToken cancellationToken)
        {
            world.Log.Add($"lock {key}");
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            world.Log.Add("save");
            return Task.FromResult(1);
        }

        public Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryLockDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class Transaction : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeIncidents(TaskWorld world) : IIncidentRepository
    {
        public void Add(Incident incident) => world.Incidents.Add(incident);

        public Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(world.Incidents.Find(incident => incident.TenantId == tenantId && incident.Id == id));

        public Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken) =>
            Task.FromResult(world.Incidents.Exists(incident => incident.TenantId == tenantId && incident.OperationKey == operationKey));

        public Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken) =>
            Task.FromResult($"NOV-{today:yyyyMMdd}-{world.Incidents.Count + 1:D4}");
    }

    private sealed class FakeTasks(TaskWorld world) : IWarehouseTaskRepository
    {
        private WarehouseTask? Find(Guid tenantId, Guid id) =>
            world.Tasks.Find(task => task.TenantId == tenantId && task.Id == id);

        public void Add(WarehouseTask task) => world.Tasks.Add(task);

        public Task<bool> LockAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
        {
            world.Log.Add("lock-task");
            return Task.FromResult(Find(tenantId, id) is not null);
        }

        public Task<WarehouseTask?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
        {
            world.Log.Add("load-task");
            var task = Find(tenantId, id);
            if (task is not null)
            {
                world.OnLoadTask?.Invoke(task);
            }

            return Task.FromResult(task);
        }

        public Task<WarehouseTask?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Find(tenantId, id));

        public Task<WarehouseTask?> FindInProgressOfMemberAsync(
            Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
            Task.FromResult(world.Tasks.Find(task =>
                task.Status == WarehouseTaskStatus.InProgress && task.ClaimedByMemberId == memberId));

        public Task<bool> HasMemberClaimedAsync(Guid tenantId, Guid taskId, Guid memberId, CancellationToken cancellationToken) =>
            Task.FromResult(Find(tenantId, taskId)?.Events.Any(value => value.Kind == TaskEventKind.Claimed && value.MemberId == memberId) == true);

        public Task<Guid?> LockNextCandidateAsync(
            Guid tenantId,
            Guid warehouseId,
            Guid memberId,
            IReadOnlyCollection<WarehouseTaskType> types,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            world.Log.Add("candidate");
            return Task.FromResult(world.Candidate);
        }

        public Task<IReadOnlyList<WarehouseTask>> ListAssignedToAsync(
            Guid tenantId, Guid warehouseId, Guid memberId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(
            [
                .. world.Tasks.Where(task =>
                    task.Status == WarehouseTaskStatus.Assigned && task.AssignedMemberId == memberId),
            ]);

        public Task<IReadOnlyList<WarehouseTask>> ListInProgressAsync(
            Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(
                [.. world.Tasks.Where(task => task.Status == WarehouseTaskStatus.InProgress)]);

        public Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(
            Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignedTaskRef>>(
            [
                .. world.Tasks
                    .Where(task => task.Status is WarehouseTaskStatus.Planned or WarehouseTaskStatus.Assigned
                        && task.AssignedMemberId is not null)
                    .OrderBy(task => task.Number, StringComparer.Ordinal)
                    .Select(task => new AssignedTaskRef(task.Id, task.Number, task.Type, task.AssignedMemberId!.Value)),
            ]);

        public Task<IReadOnlyList<TaskStatusCount>> CountByStatusAsync(
            Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStatusCount>>(
                [.. world.Tasks.GroupBy(task => task.Status).Select(group => new TaskStatusCount(group.Key, group.Count()))]);

        public Task<IReadOnlyList<TaskTypeCount>> CountOpenByTypeAsync(
            Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskTypeCount>>(
            [
                .. world.Tasks
                    .Where(task => task.Status is not (WarehouseTaskStatus.Done or WarehouseTaskStatus.Cancelled))
                    .GroupBy(task => task.Type)
                    .Select(group => new TaskTypeCount(group.Key, group.Count())),
            ]);

        public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> FindClaimedAtAsync(
            Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(world.Tasks
                .Where(task => taskIds.Contains(task.Id) && task.Events.Any(value => value.Kind == TaskEventKind.Claimed))
                .ToDictionary(
                    task => task.Id,
                    task => task.Events.Where(value => value.Kind == TaskEventKind.Claimed).Max(value => value.OccurredAt)));

        public Task<WarehouseTaskPage> ListAsync(
            Guid tenantId,
            Guid? warehouseId,
            WarehouseTaskStatus? status,
            WarehouseTaskType? type,
            Guid? assignedMemberId,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExpiredTaskLease>> LockExpiredAsync(
            DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
