using Modules.Wms.Domain.Inventory;
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

    /// <summary>La saca del pool; conserva a quién está asignada y borra el reclamo que haya quedado de un lease vencido.</summary>
    public void Hold(Guid by, string? reason, DateTimeOffset now)
    {
        EnsureStatus("hold", WarehouseTaskStatus.Ready, WarehouseTaskStatus.Assigned);
        var detail = OptionalReason(reason);

        // B1: un reclamo que quedó de un lease vencido no sobrevive a la retención. Al publicar otra vez, una confirmación tardía de
        // antes ya no la reanuda ni conserva el token (DA-35).
        ClearClaim();
        Status = WarehouseTaskStatus.Planned;
        ReleasedAt = null;
        Version++;
        Record(TaskEventKind.Held, by, now, detail: detail);
    }

    /// <summary>Reasignar una tarea en curso exige motivo y revoca el lease de quien la tiene. Asignársela a otra persona también
    /// revoca el reclamo que haya quedado de un lease vencido: su confirmación tardía ya no la reanuda.</summary>
    public void Assign(Guid memberId, Guid by, string? reason, DateTimeOffset now)
    {
        EnsureOpen("assign");
        var inProgress = Status == WarehouseTaskStatus.InProgress;
        var detail = inProgress ? RequiredReason(reason) : OptionalReason(reason);
        if (inProgress || ClaimedByMemberId != memberId)
        {
            ClearClaim();
        }

        AssignedMemberId = memberId;
        if (Status != WarehouseTaskStatus.Planned)
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
        MarkCancelled(detail, by, now, now);
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
        CloseIfFinished(by, now, now, detail);
    }

    /// <summary>Toma la tarea: cualquiera si está en el pool, sólo el asignado si está asignada. El lease es nuevo, salvo que la misma
    /// persona retome desde el mismo celular su reclamo vencido, que conserva el token para que las confirmaciones que dejó en cola
    /// sigan valiendo (DA-34).</summary>
    public void Claim(Guid memberId, string deviceId, DateTimeOffset now)
    {
        if (Status == WarehouseTaskStatus.Assigned && AssignedMemberId != memberId)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskAssignedToOther, "Esta tarea está asignada a otra persona.");
        }

        EnsureStatus("claim", WarehouseTaskStatus.Ready, WarehouseTaskStatus.Assigned);
        var device = RequiredDevice(deviceId);
        // Sólo un lease vencido deja un reclamo puesto en una tarea que se puede reclamar.
        var resumesOwnClaim = ClaimedByMemberId == memberId && ClaimedDeviceId == device && LeaseToken is not null;
        Status = WarehouseTaskStatus.InProgress;
        ClaimedByMemberId = memberId;
        ClaimedDeviceId = device;
        LeaseToken = resumesOwnClaim ? LeaseToken : Guid.NewGuid();
        Renew(now);
        Version++;
        Record(TaskEventKind.Claimed, memberId, now, deviceId: device);
    }

    public LeaseCheck CheckLease(Guid memberId, Guid leaseToken)
    {
        if (LeaseToken != leaseToken || ClaimedByMemberId != memberId)
        {
            return LeaseCheck.Lost;
        }

        return Status switch
        {
            WarehouseTaskStatus.InProgress => LeaseCheck.Valid,
            WarehouseTaskStatus.Ready => LeaseCheck.Resumable,
            WarehouseTaskStatus.Assigned when AssignedMemberId == memberId => LeaseCheck.Resumable,
            _ => LeaseCheck.Lost,
        };
    }

    /// <summary>Sólo extiende el lease: sin evento ni versión, para no invalidar la que el supervisor tiene en pantalla. No reanuda un
    /// lease vencido.</summary>
    public void Heartbeat(Guid memberId, Guid leaseToken, DateTimeOffset now)
    {
        EnsureValidLease(memberId, leaseToken);
        Renew(now);
    }

    /// <summary>Quien ya tiene la tarea en curso la vuelve a pedir («siguiente» o «reclamar»): renueva el lease con el mismo token, sin
    /// evento ni versión, como el latido. Vale también con el lease vencido mientras el barrido no haya pasado: sigue en curso y nadie
    /// más pudo tomarla.</summary>
    public void RenewClaim(Guid memberId, DateTimeOffset now)
    {
        EnsureStatus("claim", WarehouseTaskStatus.InProgress);
        if (ClaimedByMemberId != memberId)
        {
            throw new InvalidOperationException($"Task {Id} is in progress for another member.");
        }

        Renew(now);
    }

    /// <summary>El operario devuelve la tarea al pool; deja de estar asignada si lo estaba a él. Conserva su antigüedad.</summary>
    public void Unclaim(Guid memberId, Guid leaseToken, TaskUnclaimReason reason, DateTimeOffset now)
    {
        EnsureValidLease(memberId, leaseToken);
        var device = ClaimedDeviceId;
        Status = WarehouseTaskStatus.Ready;
        AssignedMemberId = null;
        ClearClaim();
        LastActivityAt = now;
        Version++;
        Record(TaskEventKind.Unclaimed, memberId, now, deviceId: device, detail: reason.ToString());
    }

    /// <summary>Devuelve la tarea al pool (o a su asignado) si el lease venció. Conserva quién la reclamó y su token: es lo que deja
    /// reanudar una confirmación tardía. Devuelve <c>true</c> si cambió algo.</summary>
    public bool ExpireLease(DateTimeOffset now)
    {
        if (Status != WarehouseTaskStatus.InProgress || LeaseExpiresAt is not { } expiresAt || expiresAt >= now)
        {
            return false;
        }

        Status = AssignedMemberId is null ? WarehouseTaskStatus.Ready : WarehouseTaskStatus.Assigned;
        LeaseExpiresAt = null;
        Version++;
        Record(TaskEventKind.LeaseExpired, null, now);
        return true;
    }

    /// <summary>Confirma parte o todo de una línea. Con un lease reanudable, reanuda la tarea antes: la mercancía ya se movió en el
    /// piso. El llamador responde antes el lease perdido (<see cref="CheckLease"/>) y la línea inexistente
    /// (<see cref="FindLine"/>).</summary>
    /// <param name="occurredAt">Cuándo se escaneó, dentro de la ventana del ledger y no más de 5 minutos antes de la publicación de la tarea; sin él, <paramref name="now"/>. Es la fecha de
    /// los hechos que la confirmación deja en la historia y la de cierre (DA-36); el lease y la actividad usan
    /// <paramref name="now"/>.</param>
    /// <param name="memberIsBusy">La persona ya tiene otra tarea en curso: una confirmación tardía se anota sin reanudar ésta, que
    /// sigue donde está y sin lease (DA-32).</param>
    public WarehouseTaskLine ConfirmLine(
        Guid memberId,
        Guid leaseToken,
        string deviceId,
        Guid lineId,
        decimal quantity,
        DateTimeOffset now,
        DateTimeOffset? occurredAt = null,
        bool memberIsBusy = false)
    {
        var (line, lease, device, scannedAt) = PrepareLineWrite(
            memberId,
            leaseToken,
            deviceId,
            lineId,
            "confirm-line",
            now,
            occurredAt,
            line =>
            {
                if (quantity <= 0)
                {
                    throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, "La cantidad confirmada tiene que ser mayor que cero.");
                }

                if (quantity > line.Remaining)
                {
                    throw new WmsDomainException(
                        WmsErrorCodes.TaskQuantityExceedsRemaining, "La cantidad confirmada supera lo que falta de la línea.");
                }
            });

        // Después de validar: una confirmación rechazada no reanuda nada.
        var staysReleased = ApplyLease(lease, memberIsBusy, memberId, device, scannedAt, now);
        line.Confirm(quantity);
        FinishLineWrite(staysReleased, now);
        Version++;
        Record(TaskEventKind.LineConfirmed, memberId, scannedAt, deviceId: device, lineId: lineId, quantity: quantity, recordedAt: now);
        CloseIfFinished(memberId, scannedAt, now);
        return line;
    }

    /// <summary>De lo que falta de la línea sólo había <paramref name="quantityFound"/> (puede ser cero). Lo encontrado se confirma
    /// como en <see cref="ConfirmLine"/>; la línea queda <c>Short</c> y la tarea se cierra si era la última abierta. Mismo lease,
    /// misma reanudación, misma regla de DA-32 y misma ventana y piso de <paramref name="occurredAt"/> que
    /// <see cref="ConfirmLine"/>. La diferencia no se ajusta en el piso: la propone el conteo (DA-18).</summary>
    public WarehouseTaskLine ShortLine(
        Guid memberId,
        Guid leaseToken,
        string deviceId,
        Guid lineId,
        decimal quantityFound,
        DateTimeOffset now,
        DateTimeOffset? occurredAt = null,
        bool memberIsBusy = false)
    {
        var (line, lease, device, scannedAt) = PrepareLineWrite(
            memberId,
            leaseToken,
            deviceId,
            lineId,
            "short-line",
            now,
            occurredAt,
            line =>
            {
                if (quantityFound < 0)
                {
                    throw new WmsDomainException(WmsErrorCodes.TaskLineInvalid, "La cantidad encontrada no puede ser negativa.");
                }

                if (quantityFound > line.Remaining)
                {
                    throw new WmsDomainException(
                        WmsErrorCodes.TaskQuantityExceedsRemaining, "La cantidad encontrada supera lo que falta de la línea.");
                }

                // Encontrar todo no es un faltante: eso es confirmar.
                if (quantityFound == line.Remaining)
                {
                    throw new WmsDomainException(
                        WmsErrorCodes.TaskLineInvalid,
                        "Si encontraste todo lo que falta, confirma la línea en vez de declarar un faltante.");
                }
            });

        var staysReleased = ApplyLease(lease, memberIsBusy, memberId, device, scannedAt, now);
        if (quantityFound > 0)
        {
            line.Confirm(quantityFound);
        }

        FinishLineWrite(staysReleased, now);
        Version++;
        if (quantityFound > 0)
        {
            Record(
                TaskEventKind.LineConfirmed, memberId, scannedAt, deviceId: device, lineId: lineId, quantity: quantityFound, recordedAt: now);
        }

        var missing = line.Remaining;
        line.MarkShort();
        Record(TaskEventKind.LineShort, memberId, scannedAt, deviceId: device, lineId: lineId, quantity: missing, recordedAt: now);
        CloseIfFinished(memberId, scannedAt, now);
        return line;
    }

    /// <summary>Lo que comparten las acciones del operario sobre una línea: lease perdido (el llamador ya lo respondió), celular,
    /// línea abierta, la validación de cantidades propia de la acción, la ventana del ledger y el piso de la publicación. No cambia
    /// nada.</summary>
    private (WarehouseTaskLine Line, LeaseCheck Lease, string Device, DateTimeOffset ScannedAt) PrepareLineWrite(
        Guid memberId,
        Guid leaseToken,
        string deviceId,
        Guid lineId,
        string action,
        DateTimeOffset now,
        DateTimeOffset? occurredAt,
        Action<WarehouseTaskLine> validateQuantity)
    {
        var lease = CheckLease(memberId, leaseToken);
        if (lease == LeaseCheck.Lost)
        {
            throw new InvalidOperationException($"Task {Id} cannot act on a line with a lost lease.");
        }

        var device = RequiredDevice(deviceId);
        var line = LineOf(lineId);
        if (line.Status != TaskLineStatus.Open)
        {
            throw new TaskTransitionException(line.Status.ToString(), action);
        }

        validateQuantity(line);

        // timestamptz sólo admite offset cero.
        var scannedAt = (occurredAt ?? now).ToUniversalTime();
        LedgerEntryRules.EnsureOccurredAtInWindow(scannedAt, now);

        // B2: un escaneo muy anterior a la publicación es imposible (sin publicar, a la creación); la holgura es la del reloj del
        // ledger, para que un celular atrasado no se quede con un 422 permanente. Mismo código que la ventana del ledger.
        if (scannedAt < (ReleasedAt ?? CreatedAt).AddMinutes(-LedgerEntryRules.ClockToleranceMinutes))
        {
            throw new WmsDomainException(
                WmsErrorCodes.MovementOccurredAtOutOfRange,
                "La fecha del escaneo no puede ser anterior a la publicación de la tarea por más de 5 minutos.");
        }

        return (line, lease, device, scannedAt);
    }

    /// <summary>Con un lease reanudable y una persona libre, la tarea vuelve a <c>InProgress</c>. Devuelve <c>true</c> si se queda
    /// donde estaba (DA-32).</summary>
    private bool ApplyLease(
        LeaseCheck lease, bool memberIsBusy, Guid memberId, string device, DateTimeOffset scannedAt, DateTimeOffset now)
    {
        var staysReleased = lease == LeaseCheck.Resumable && memberIsBusy;
        if (lease == LeaseCheck.Resumable && !memberIsBusy)
        {
            Status = WarehouseTaskStatus.InProgress;
            Record(TaskEventKind.LeaseResumed, memberId, scannedAt, deviceId: device, recordedAt: now);
        }

        return staysReleased;
    }

    private void FinishLineWrite(bool staysReleased, DateTimeOffset now)
    {
        if (staysReleased)
        {
            LastActivityAt = now;
        }
        else
        {
            Renew(now);
        }
    }

    private void Release(Guid by, DateTimeOffset now)
    {
        Status = AssignedMemberId is null ? WarehouseTaskStatus.Ready : WarehouseTaskStatus.Assigned;
        ReleasedAt = now;
        Record(TaskEventKind.Published, by, now);
    }

    /// <summary>Sin líneas abiertas la tarea se cierra: <c>Done</c> si alguna quedó <c>Done</c> o <c>Short</c>, <c>Cancelled</c> si se
    /// cancelaron todas. <paramref name="cancelReason"/> sólo falta al confirmar o declarar un faltante, donde siempre queda al menos
    /// una <c>Done</c> o <c>Short</c> si no hay abiertas.
    /// </summary>
    private void CloseIfFinished(Guid by, DateTimeOffset now, DateTimeOffset recordedAt, string? cancelReason = null)
    {
        if (_lines.Exists(line => line.Status == TaskLineStatus.Open))
        {
            return;
        }

        if (_lines.Exists(line => line.Status is TaskLineStatus.Done or TaskLineStatus.Short))
        {
            Status = WarehouseTaskStatus.Done;
            CompletedAt = now;
            ClearClaim();
            Record(TaskEventKind.Completed, by, now, recordedAt: recordedAt);
            return;
        }

        MarkCancelled(cancelReason ?? throw new InvalidOperationException($"Task {Id} has no confirmed line."), by, now, recordedAt);
    }

    private void MarkCancelled(string reason, Guid by, DateTimeOffset now, DateTimeOffset recordedAt)
    {
        Status = WarehouseTaskStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        ClearClaim();
        Record(TaskEventKind.Cancelled, by, now, detail: reason, recordedAt: recordedAt);
    }

    /// <summary>Extiende el lease desde <paramref name="now"/> y marca la actividad.</summary>
    private void Renew(DateTimeOffset now)
    {
        LeaseExpiresAt = now + TaskTypePolicy.LeaseDuration(Type);
        LastActivityAt = now;
    }

    private void ClearClaim()
    {
        ClaimedByMemberId = null;
        ClaimedDeviceId = null;
        LeaseToken = null;
        LeaseExpiresAt = null;
    }

    private void EnsureValidLease(Guid memberId, Guid leaseToken)
    {
        if (CheckLease(memberId, leaseToken) != LeaseCheck.Valid)
        {
            throw new InvalidOperationException($"Task {Id} has no valid lease for this member and token.");
        }
    }

    private static string RequiredDevice(string deviceId)
    {
        var trimmed = (deviceId ?? string.Empty).Trim();
        return trimmed.Length is 0 or > DeviceIdMaxLength
            ? throw new ArgumentException($"The device id has between 1 and {DeviceIdMaxLength} characters.", nameof(deviceId))
            : trimmed;
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

    private static string RequiredReason(string? reason) =>
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
        string? detail = null,
        DateTimeOffset? recordedAt = null) =>
        _events.Add(WarehouseTaskEvent.Record(TenantId, Id, kind, memberId, deviceId, lineId, quantity, detail, at, recordedAt ?? at));
}
