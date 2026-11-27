using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>
/// Una novedad: algo que el operario o el sistema encontró distinto de lo esperado (spec 2026-11-26 §8). Vive en el área
/// <c>Control</c>, junto al ajuste, y conoce las tareas sólo por <see cref="Guid"/>: las tareas no saben de novedades. Cada
/// transición sube la versión, que es el token de concurrencia (412) de las acciones del supervisor.
/// </summary>
public sealed class Incident
{
    public const int NoteMaxLength = 500;
    public const int RawCodeMaxLength = 200;
    public const int MaxEvidence = 5;

    private readonly List<IncidentEvidence> _evidence = [];
    private readonly List<IncidentEvent> _events = [];

    private Incident()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    public OwnerId OwnerId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public IncidentType Type { get; private set; }

    public IncidentStatus Status { get; private set; }

    public Guid? SourceTaskId { get; private set; }

    public Guid? SourceTaskLineId { get; private set; }

    /// <summary>El <c>Count</c> que verifica lo declarado, si la novedad pidió uno.</summary>
    public Guid? VerificationTaskId { get; private set; }

    public LocationId? LocationId { get; private set; }

    public Guid? VariantId { get; private set; }

    public LotId? LotId { get; private set; }

    public StockStatus? StockStatus { get; private set; }

    public decimal? ExpectedQty { get; private set; }

    public decimal? ActualQty { get; private set; }

    /// <summary>Lo que se escaneó y no se pudo leer o resolver.</summary>
    public string? RawCode { get; private set; }

    /// <summary>JSON tal cual llegó (jsonb): la acción rechazada de un <c>SyncConflict</c>.</summary>
    public string? RawPayload { get; private set; }

    public string? Note { get; private set; }

    /// <summary>La membresía que la reportó; <see cref="Guid.Empty"/> es el sistema.</summary>
    public Guid ReportedBy { get; private set; }

    public string? DeviceId { get; private set; }

    public DateTimeOffset ReportedAt { get; private set; }

    /// <summary>La <c>Idempotency-Key</c> de la acción que la originó: deduplica un <c>SyncConflict</c> reintentado.</summary>
    public string? OperationKey { get; private set; }

    public IncidentResolution? Resolution { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? ResolutionNote { get; private set; }

    public Guid? BatchId { get; private set; }

    public Guid? AdjustmentId { get; private set; }

    public Guid? ParentIncidentId { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<IncidentEvidence> Evidence => _evidence;

    /// <summary>Sólo se agrega: la historia de la novedad.</summary>
    public IReadOnlyList<IncidentEvent> Events => _events;

    /// <summary>El <paramref name="number"/> lo asigna quien reporta, dentro de la transacción que numera. <paramref name="reportedAt"/>
    /// es la hora del escaneo; <paramref name="now"/>, la del servidor. <paramref name="reportedBy"/> es una membresía, o
    /// <see cref="Guid.Empty"/> si la reporta el sistema.</summary>
    public static Incident Report(
        Guid tenantId,
        string number,
        WarehouseId warehouse,
        OwnerId owner,
        IncidentType type,
        IncidentSubject subject,
        string? note,
        string? rawCode,
        string? rawPayload,
        IReadOnlyList<IncidentEvidence> evidence,
        Guid reportedBy,
        string? deviceId,
        string? operationKey,
        DateTimeOffset reportedAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(evidence);

        var trimmedNote = Trimmed(note, NoteMaxLength, "La nota");
        var trimmedCode = Trimmed(rawCode, RawCodeMaxLength, "El código escaneado");
        if (subject.ExpectedQty < 0 || subject.ActualQty < 0)
        {
            throw new WmsDomainException(WmsErrorCodes.IncidentInvalid, "Las cantidades de una novedad no pueden ser negativas.");
        }

        if (evidence.Count > MaxEvidence)
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentEvidenceInvalid, $"Una novedad lleva a lo sumo {MaxEvidence} evidencias.");
        }

        var at = reportedAt.ToUniversalTime();
        var incident = new Incident
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            WarehouseId = warehouse,
            OwnerId = owner,
            Number = number,
            Type = type,
            Status = IncidentStatus.Open,
            SourceTaskId = subject.SourceTaskId,
            SourceTaskLineId = subject.SourceTaskLineId,
            LocationId = subject.Location,
            VariantId = subject.VariantId,
            LotId = subject.LotId,
            StockStatus = subject.StockStatus,
            ExpectedQty = subject.ExpectedQty,
            ActualQty = subject.ActualQty,
            RawCode = trimmedCode,
            RawPayload = string.IsNullOrWhiteSpace(rawPayload) ? null : rawPayload,
            Note = trimmedNote,
            ReportedBy = reportedBy,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            ReportedAt = at,
            OperationKey = string.IsNullOrWhiteSpace(operationKey) ? null : operationKey,
            Version = 1,
        };
        for (var index = 0; index < evidence.Count; index++)
        {
            evidence[index].Place(index);
            incident._evidence.Add(evidence[index]);
        }

        incident._events.Add(IncidentEvent.Record(
            tenantId, incident.Id, IncidentEventKind.Reported, reportedBy == Guid.Empty ? null : reportedBy, null, at, now));
        return incident;
    }

    /// <summary>Enlaza el <c>Count</c> que verifica lo declarado (el que ya estaba abierto sobre la misma clave, o el que esta misma
    /// transacción acaba de crear). Se llama al nacer la novedad, antes de guardarla: no sube la versión, porque ningún supervisor
    /// la ha visto. <paramref name="taskNumber"/> es sólo para la historia.</summary>
    public void LinkVerification(Guid countTaskId, DateTimeOffset now, string? taskNumber = null)
    {
        EnsureUnresolved("link-verification");
        VerificationTaskId = countTaskId;
        _events.Add(IncidentEvent.Record(
            TenantId, Id, IncidentEventKind.VerificationLinked, null, taskNumber, now, now));
    }

    /// <summary>El saldo cambió desde el conteo y el sistema pidió uno nuevo (decisión del owner, 2026-11-27): la novedad pasa a
    /// apuntarle, en cualquier estado, porque la separación de funciones se deriva de las novedades que un <c>Count</c> verifica. Sube
    /// la versión: un supervisor ya pudo haberla visto.</summary>
    public void RelinkVerification(Guid countTaskId, string? taskNumber, DateTimeOffset now)
    {
        VerificationTaskId = countTaskId;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.VerificationLinked, null, taskNumber, now, now));
    }

    /// <summary>El <c>Count</c> que la verificaba se canceló: deja de apuntarle y la novedad sigue abierta para que el supervisor la
    /// acepte (la próxima novedad sobre la clave genera otro conteo). Sube la versión: un supervisor ya pudo haberla visto.</summary>
    public void UnlinkVerification(string taskNumber, DateTimeOffset now)
    {
        EnsureUnresolved("unlink-verification");
        VerificationTaskId = null;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.VerificationCancelled, null, taskNumber, now, now));
    }

    /// <summary>Lo resuelve el sistema (<see cref="Guid.Empty"/>, sin persona en el evento) con una resolución que el tipo admite, por
    /// el resultado de un conteo. <paramref name="parentIncidentId"/> es la <c>CountVariance</c> que la sigue, si el conteo encontró
    /// una diferencia.</summary>
    public void ResolveBySystem(IncidentResolution resolution, string note, Guid? parentIncidentId, DateTimeOffset now)
    {
        EnsureCanResolve(resolution);
        Status = IncidentStatus.Resolved;
        Resolution = resolution;
        ResolvedBy = Guid.Empty;
        ResolvedAt = now.ToUniversalTime();
        ResolutionNote = Trimmed(note, NoteMaxLength, "La nota de la resolución");
        ParentIncidentId = parentIncidentId;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.Resolved, null, resolution.ToString(), now, now));
    }

    /// <summary>El ajuste de esta <c>CountVariance</c> se contabilizó: la novedad queda resuelta con <c>Accept</c> y el lote del ledger.
    /// Sólo la cierra su ajuste (un supervisor no la resuelve a mano). <paramref name="by"/> es quien contabilizó.</summary>
    public void ResolveByAdjustment(Guid batchId, Guid by, string adjustmentNumber, DateTimeOffset now)
    {
        EnsureCountVariance();
        EnsureUnresolved("resolve");
        Status = IncidentStatus.Resolved;
        Resolution = IncidentResolution.Accept;
        ResolvedBy = by;
        ResolvedAt = now.ToUniversalTime();
        ResolutionNote = Trimmed($"Ajuste {adjustmentNumber} contabilizado.", NoteMaxLength, "La nota de la resolución");
        BatchId = batchId;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.Resolved, by, IncidentResolution.Accept.ToString(), now, now));
    }

    /// <summary>Se rechazó el ajuste de esta <c>CountVariance</c>: la novedad se rechaza con el motivo del ajuste. Sólo ese tipo, y sólo
    /// por su ajuste.</summary>
    public void RejectByAdjustment(Guid by, string note, DateTimeOffset now)
    {
        EnsureCountVariance();
        EnsureUnresolved("reject");
        var trimmed = Trimmed(note, NoteMaxLength, "El motivo del rechazo")
            ?? throw new WmsDomainException(WmsErrorCodes.IncidentInvalid, "El motivo del rechazo es obligatorio.");
        Status = IncidentStatus.Rejected;
        ResolvedBy = by;
        ResolvedAt = now.ToUniversalTime();
        ResolutionNote = trimmed;
        Version++;
        _events.Add(IncidentEvent.Record(
            TenantId, Id, IncidentEventKind.Rejected, by == Guid.Empty ? null : by, null, now, now));
    }

    /// <summary>Separación de funciones (DA-17, DA-47): quien contó, o quien reportó una de las novedades que este conteo cerró, no
    /// aprueba el ajuste que su diferencia provocó. Se comparan membresías, nunca <c>SubjectId</c>.</summary>
    public void EnsureNotInvolvedInApproval(Guid member, IReadOnlyCollection<Guid> reportersOfVerified)
    {
        ArgumentNullException.ThrowIfNull(reportersOfVerified);
        EnsureCountVariance();
        if (member != Guid.Empty && (member == ReportedBy || reportersOfVerified.Contains(member)))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentSelfResolution, "Quien contó o reportó el faltante no puede aprobar el ajuste que provocó.");
        }
    }

    private void EnsureCountVariance()
    {
        if (Type != IncidentType.CountVariance)
        {
            throw new InvalidOperationException($"Only a {nameof(IncidentType.CountVariance)} is closed by its adjustment, not a {Type}.");
        }
    }

    /// <summary>El ajuste que propuso el conteo y que cerrará esta <c>CountVariance</c>. Sólo ese tipo lo lleva; se llama al nacer la
    /// novedad, antes de guardarla.</summary>
    public void AttachAdjustment(Guid adjustmentId)
    {
        if (Type != IncidentType.CountVariance)
        {
            throw new InvalidOperationException($"Only a {nameof(IncidentType.CountVariance)} carries an adjustment, not a {Type}.");
        }

        AdjustmentId = adjustmentId;
    }

    /// <summary>Un supervisor la toma: <c>Open</c> pasa a <c>InReview</c> (spec §8.3).</summary>
    public void Review(Guid by, DateTimeOffset now)
    {
        if (Status != IncidentStatus.Open)
        {
            throw new InvalidTransitionException(Status.ToString(), "review");
        }

        Status = IncidentStatus.InReview;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.InReview, by, null, now, now));
    }

    /// <summary>La cierra con una resolución que el tipo admite. <c>CountVariance</c> nunca se resuelve a mano: la cierra su ajuste.
    /// <paramref name="batchId"/> es el lote del ledger si la resolución movió stock.</summary>
    public void Resolve(IncidentResolution resolution, Guid by, string? note, Guid? batchId, DateTimeOffset now)
    {
        EnsureCanResolve(resolution);
        var trimmed = Trimmed(note, NoteMaxLength, "La nota de la resolución");
        Status = IncidentStatus.Resolved;
        Resolution = resolution;
        ResolvedBy = by;
        ResolvedAt = now.ToUniversalTime();
        ResolutionNote = trimmed;
        BatchId = batchId;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.Resolved, by, resolution.ToString(), now, now));
    }

    /// <summary>Lo que <see cref="Resolve"/> comprueba antes de cambiar nada: el handler lo llama antes de armar el movimiento del
    /// ledger, para que una resolución que el tipo no admite no llegue a mover stock (y deshacerlo sería trabajo tirado).</summary>
    public void EnsureCanResolve(IncidentResolution resolution)
    {
        EnsureUnresolved("resolve");
        if (!IncidentCatalog.ResolutionsOf(Type).Contains(resolution))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentResolutionNotAllowed, $"Una novedad {Type} no se resuelve con {resolution}.");
        }
    }

    /// <summary>La descarta. Sólo una novedad que no movió nada (<see cref="IncidentCatalog.CanBeRejected"/>); la nota es obligatoria.</summary>
    public void Reject(Guid by, string note, DateTimeOffset now)
    {
        EnsureUnresolved("reject");
        if (!IncidentCatalog.CanBeRejected(Type))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentResolutionNotAllowed, $"Una novedad {Type} no se rechaza.");
        }

        var trimmed = Trimmed(note, NoteMaxLength, "El motivo del rechazo")
            ?? throw new WmsDomainException(WmsErrorCodes.IncidentInvalid, "El motivo del rechazo es obligatorio.");
        Status = IncidentStatus.Rejected;
        ResolvedBy = by;
        ResolvedAt = now.ToUniversalTime();
        ResolutionNote = trimmed;
        Version++;
        _events.Add(IncidentEvent.Record(TenantId, Id, IncidentEventKind.Rejected, by, null, now, now));
    }

    /// <summary>Separación de funciones (DA-17, DA-47): quien reportó no acepta en el stock su propia novedad, porque eso mueve stock.
    /// Se comparan membresías, nunca <c>SubjectId</c>.</summary>
    public void EnsureNotSelfResolution(IncidentResolution resolution, Guid by)
    {
        if (resolution == IncidentResolution.AcceptIntoStock && ReportedBy != Guid.Empty && ReportedBy == by)
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentSelfResolution, "Quien reportó la novedad no puede aceptarla en el stock.");
        }
    }

    private void EnsureUnresolved(string action)
    {
        if (Status is not (IncidentStatus.Open or IncidentStatus.InReview))
        {
            throw new InvalidTransitionException(Status.ToString(), action);
        }
    }

    private static string? Trimmed(string? value, int maxLength, string what)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed?.Length > maxLength
            ? throw new WmsDomainException(WmsErrorCodes.IncidentInvalid, $"{what} tiene a lo sumo {maxLength} caracteres.")
            : trimmed;
    }
}
