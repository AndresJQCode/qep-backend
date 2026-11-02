using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>Un ajuste manual de inventario: se propone, se aprueba y se contabiliza contra el ledger, o se rechaza. Cada transición sube
/// la versión, que es el token de concurrencia y parte de la clave de idempotencia del <c>post</c>.</summary>
public sealed class Adjustment
{
    private readonly List<AdjustmentLine> _lines = [];

    private Adjustment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public OwnerId OwnerId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public AdjustmentReason Reason { get; private set; }

    public AdjustmentStatus Status { get; private set; }

    public string? Note { get; private set; }

    /// <summary>S12: la fecha física del ajuste. <c>null</c> es la hora del <c>post</c>.</summary>
    public DateTimeOffset? OccurredAt { get; private set; }

    public Guid ProposedBy { get; private set; }

    public DateTimeOffset ProposedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    public Guid? RejectedBy { get; private set; }

    public string? RejectNote { get; private set; }

    public Guid? BatchId { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<AdjustmentLine> Lines => _lines;

    public const int MaxLines = 500;

    public const int NoteMaxLength = 500;

    /// <summary>El <paramref name="number"/> lo asigna quien propone, dentro de la transacción que numera (S26). La fecha física, si
    /// viene, tiene que estar en la ventana de S12: se valida acá y el ledger la vuelve a validar al contabilizar.</summary>
    public static Adjustment Propose(
        Guid tenantId,
        string number,
        OwnerId owner,
        WarehouseId warehouse,
        AdjustmentReason reason,
        string? note,
        DateTimeOffset? occurredAt,
        IReadOnlyList<AdjustmentLine> lines,
        Guid proposedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        if (lines.Count is < 1 or > MaxLines)
        {
            throw new WmsDomainException(WmsErrorCodes.AdjustmentLineInvalid, $"Un ajuste lleva entre 1 y {MaxLines} líneas.");
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote?.Length > NoteMaxLength)
        {
            throw new ArgumentException($"The note has at most {NoteMaxLength} characters.", nameof(note));
        }

        if (occurredAt is { } physical)
        {
            LedgerEntryRules.EnsureOccurredAtInWindow(physical, now);
        }

        var adjustment = new Adjustment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            OwnerId = owner,
            WarehouseId = warehouse,
            Reason = reason,
            Status = AdjustmentStatus.Proposed,
            Note = trimmedNote,
            OccurredAt = occurredAt?.ToUniversalTime(),
            ProposedBy = proposedBy,
            ProposedAt = now,
            Version = 1,
        };
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index].Place(index);
            adjustment._lines.Add(lines[index]);
        }

        return adjustment;
    }

    public void Approve(Guid by, DateTimeOffset now)
    {
        EnsureStatus(AdjustmentStatus.Proposed, "approve");
        Status = AdjustmentStatus.Approved;
        ApprovedBy = by;
        ApprovedAt = now;
        Version++;
    }

    public void Reject(Guid by, string note, DateTimeOffset now)
    {
        if (Status is not (AdjustmentStatus.Proposed or AdjustmentStatus.Approved))
        {
            throw new InvalidTransitionException(Status.ToString(), "reject");
        }

        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > NoteMaxLength)
        {
            throw new WmsDomainException(
                WmsErrorCodes.AdjustmentLineInvalid, $"El motivo del rechazo es obligatorio y tiene a lo sumo {NoteMaxLength} caracteres.");
        }

        Status = AdjustmentStatus.Rejected;
        RejectedBy = by;
        RejectedAt = now;
        RejectNote = trimmed;
        Version++;
    }

    /// <summary>Lo llama el handler antes de armar los movimientos: un ajuste sin aprobar no debe llegar al ledger, y deshacerlo al
    /// fallar <see cref="MarkPosted"/> sería trabajo tirado.</summary>
    public void EnsurePostable() => EnsureStatus(AdjustmentStatus.Approved, "post");

    public void MarkPosted(Guid batchId, DateTimeOffset now)
    {
        EnsurePostable();
        Status = AdjustmentStatus.Posted;
        BatchId = batchId;
        PostedAt = now;
        Version++;
    }

    private void EnsureStatus(AdjustmentStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new InvalidTransitionException(Status.ToString(), action);
        }
    }
}
