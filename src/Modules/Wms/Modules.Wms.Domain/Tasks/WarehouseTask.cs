using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Tasks;

/// <summary>Una tarea de bodega (spec 2026-11-26 §6): una intención de mover mercancía, con sus líneas y su historia. Nunca mueve stock
/// por sí misma. Cada transición sube la versión, que es el token de concurrencia, y deja un evento.</summary>
public sealed class WarehouseTask
{
    public const int MaxLines = 100;
    public const int MinPriority = 1;
    public const int MaxPriority = 9;
    public const int NoteMaxLength = 500;
    public const int ReasonMaxLength = 500;
    public const int DeviceIdMaxLength = 64;

    private readonly List<WarehouseTaskLine> _lines = [];
    private readonly List<WarehouseTaskEvent> _events = [];

    private WarehouseTask()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    /// <summary>Un dueño por tarea: nunca se mezcla mercancía de dos clientes en un recorrido.</summary>
    public OwnerId OwnerId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public WarehouseTaskType Type { get; private set; }

    public WarehouseTaskStatus Status { get; private set; }

    public int Priority { get; private set; }

    public bool Urgent { get; private set; }

    public DateTimeOffset? DueAt { get; private set; }

    /// <summary>Desde cuándo está en el pool; alimenta el envejecimiento de la prioridad.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    public TaskSourceType SourceType { get; private set; }

    public Guid? SourceId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>La membresía a la que el supervisor se la asignó.</summary>
    public Guid? AssignedMemberId { get; private set; }

    /// <summary>La membresía que la reclamó.</summary>
    public Guid? ClaimedByMemberId { get; private set; }

    public string? ClaimedDeviceId { get; private set; }

    /// <summary>Token de exclusión: cambia en cada reclamo.</summary>
    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public DateTimeOffset? LastActivityAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<WarehouseTaskLine> Lines => _lines;

    /// <summary>Sólo se agrega. Al cargar la tarea para cambiarla no vienen los anteriores.</summary>
    public IReadOnlyList<WarehouseTaskEvent> Events => _events;

    /// <summary>El <paramref name="number"/> lo asigna quien crea, dentro de la transacción que numera. Con <paramref name="publish"/>
    /// nace en el pool (o asignada); sin él, planeada.</summary>
    public static WarehouseTask CreateMove(
        Guid tenantId,
        string number,
        WarehouseId warehouse,
        OwnerId owner,
        int? priority,
        DateTimeOffset? dueAt,
        string? note,
        IReadOnlyList<WarehouseTaskLine> lines,
        Guid? assignTo,
        bool publish,
        Guid createdBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        if (lines.Count is < 1 or > MaxLines)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, $"Una tarea lleva entre 1 y {MaxLines} líneas.");
        }

        if (lines.Any(line => line.ToLocationId is null))
        {
            throw new WmsDomainException(
                WmsErrorCodes.TaskLocationInvalid, "Cada línea de un movimiento interno necesita una ubicación de destino.");
        }

        var effectivePriority = priority ?? TaskTypePolicy.DefaultPriority(WarehouseTaskType.Move);
        EnsurePriority(effectivePriority);

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote?.Length > NoteMaxLength)
        {
            throw new ArgumentException($"The note has at most {NoteMaxLength} characters.", nameof(note));
        }

        var task = new WarehouseTask
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            WarehouseId = warehouse,
            OwnerId = owner,
            Number = number,
            Type = WarehouseTaskType.Move,
            Status = WarehouseTaskStatus.Planned,
            Priority = effectivePriority,
            DueAt = dueAt?.ToUniversalTime(),
            SourceType = TaskSourceType.Manual,
            Note = trimmedNote,
            AssignedMemberId = assignTo,
            CreatedBy = createdBy,
            CreatedAt = now,
            Version = 1,
        };
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index].Place(index);
            task._lines.Add(lines[index]);
        }

        task.Record(TaskEventKind.Created, createdBy, now);

        // Publicar al crear no es otra transición: la tarea nace con la versión 1.
        if (publish)
        {
            task.Release(createdBy, now);
        }

        return task;
    }

    public WarehouseTaskLine? FindLine(Guid lineId) => _lines.Find(line => line.Id == lineId);

    public void Publish(Guid by, DateTimeOffset now)
    {
        EnsureStatus("publish", WarehouseTaskStatus.Planned);
        Release(by, now);
        Version++;
    }

    /// <summary>La saca del pool; conserva a quién está asignada.</summary>
    public void Hold(Guid by, string? reason, DateTimeOffset now)
    {
        EnsureStatus("hold", WarehouseTaskStatus.Ready, WarehouseTaskStatus.Assigned);
        var detail = OptionalReason(reason);
        Status = WarehouseTaskStatus.Planned;
        ReleasedAt = null;
        Version++;
        Record(TaskEventKind.Held, by, now, detail: detail);
    }

    public void Assign(Guid memberId, Guid by, string? reason, DateTimeOffset now)
    {
        EnsureStatus("assign", WarehouseTaskStatus.Planned, WarehouseTaskStatus.Ready, WarehouseTaskStatus.Assigned);
        var detail = OptionalReason(reason);
        AssignedMemberId = memberId;
        if (Status == WarehouseTaskStatus.Ready)
        {
            Status = WarehouseTaskStatus.Assigned;
        }

        Version++;
        Record(TaskEventKind.Assigned, by, now, detail: detail);
    }

    public void Unassign(Guid by, DateTimeOffset now)
    {
        EnsureStatus("unassign", WarehouseTaskStatus.Planned, WarehouseTaskStatus.Assigned);

        // Una planeada sin asignado no tiene qué quitar.
        if (AssignedMemberId is null)
        {
            throw new TaskTransitionException(Status.ToString(), "unassign");
        }

        AssignedMemberId = null;
        if (Status == WarehouseTaskStatus.Assigned)
        {
            Status = WarehouseTaskStatus.Ready;
        }

        Version++;
        Record(TaskEventKind.Unassigned, by, now);
    }

    public void ChangePriority(int priority, bool urgent, Guid by, DateTimeOffset now)
    {
        EnsureOpen("change-priority");
        EnsurePriority(priority);
        Priority = priority;
        Urgent = urgent;
        Version++;
        Record(TaskEventKind.PriorityChanged, by, now);
    }

    /// <summary>Cancela la tarea y sus líneas abiertas. Es terminal.</summary>
    public void Cancel(string reason, Guid by, DateTimeOffset now)
    {
        EnsureOpen("cancel");
        var detail = RequiredReason(reason);
        foreach (var line in _lines.Where(line => line.Status == TaskLineStatus.Open))
        {
            line.Cancel(detail);
        }

        Version++;
        MarkCancelled(detail, by, now);
    }

    /// <summary>Si era la última línea abierta, la tarea se cierra.</summary>
    public void CancelLine(Guid lineId, string reason, Guid by, DateTimeOffset now)
    {
        EnsureOpen("cancel-line");
        var line = LineOf(lineId);
        if (line.Status != TaskLineStatus.Open)
        {
            throw new TaskTransitionException(line.Status.ToString(), "cancel-line");
        }

        var detail = RequiredReason(reason);
        line.Cancel(detail);
        Version++;
        Record(TaskEventKind.LineCancelled, by, now, lineId: lineId, detail: detail);
        CloseIfFinished(detail, by, now);
    }

    private void Release(Guid by, DateTimeOffset now)
    {
        Status = AssignedMemberId is null ? WarehouseTaskStatus.Ready : WarehouseTaskStatus.Assigned;
        ReleasedAt = now;
        Record(TaskEventKind.Published, by, now);
    }

    private void CloseIfFinished(string reason, Guid by, DateTimeOffset now)
    {
        if (_lines.Exists(line => line.Status == TaskLineStatus.Open))
        {
            return;
        }

        MarkCancelled(reason, by, now);
    }

    private void MarkCancelled(string reason, Guid by, DateTimeOffset now)
    {
        Status = WarehouseTaskStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        Record(TaskEventKind.Cancelled, by, now, detail: reason);
    }

    /// <summary>El llamador comprueba antes con <see cref="FindLine"/> y responde 404: llegar acá con otra línea es un error de
    /// cableado.</summary>
    private WarehouseTaskLine LineOf(Guid lineId) =>
        FindLine(lineId) ?? throw new InvalidOperationException($"Line {lineId} does not belong to task {Id}.");

    private void EnsureStatus(string action, params ReadOnlySpan<WarehouseTaskStatus> allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new TaskTransitionException(Status.ToString(), action);
        }
    }

    /// <summary>Cualquier estado que no sea terminal.</summary>
    private void EnsureOpen(string action) =>
        EnsureStatus(
            action,
            WarehouseTaskStatus.Planned,
            WarehouseTaskStatus.Ready,
            WarehouseTaskStatus.Assigned,
            WarehouseTaskStatus.InProgress);

    private static void EnsurePriority(int priority)
    {
        if (priority is < MinPriority or > MaxPriority)
        {
            throw new WmsDomainException(
                WmsErrorCodes.TaskLineInvalid, $"La prioridad de una tarea va de {MinPriority} a {MaxPriority}.");
        }
    }

    private static string RequiredReason(string reason) =>
        OptionalReason(reason)
        ?? throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, "Escribe el motivo.");

    private static string? OptionalReason(string? reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return trimmed?.Length > ReasonMaxLength
            ? throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, $"El motivo tiene a lo sumo {ReasonMaxLength} caracteres.")
            : trimmed;
    }

    private void Record(
        TaskEventKind kind,
        Guid? memberId,
        DateTimeOffset at,
        string? deviceId = null,
        Guid? lineId = null,
        decimal? quantity = null,
        string? detail = null) =>
        _events.Add(WarehouseTaskEvent.Record(TenantId, Id, kind, memberId, deviceId, lineId, quantity, detail, at));
}
