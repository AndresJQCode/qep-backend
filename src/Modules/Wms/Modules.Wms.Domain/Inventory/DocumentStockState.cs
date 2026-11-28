namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// La fila por documento del inventario básico (spec §5.2): el estado de su máquina, la última versión vista y lo necesario para
/// deshacer exactamente lo que se asentó (bodega, ubicación, lote del asiento y su <c>occurred_at</c>, porque los movimientos están
/// particionados por mes). Todo evento visto la escribe o la actualiza, aunque no tenga efecto: así un cancelado que llegó antes que
/// su creado deja <see cref="LastVersion"/> y el creado viejo no reserva.
/// </summary>
public sealed class DocumentStockState
{
    private readonly List<Guid> _skippedLineIds = [];

    private DocumentStockState()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Uno de <see cref="InventoryDocumentTypes"/>.</summary>
    public string DocumentType { get; private set; } = string.Empty;

    public Guid DocumentId { get; private set; }

    public long LastVersion { get; private set; }

    public DocumentStockStatus Status { get; private set; }

    public Guid? WarehouseId { get; private set; }

    public Guid? LocationId { get; private set; }

    /// <summary>El lote del asiento que dejó el documento: la compensación revierte exactamente sus movimientos.</summary>
    public Guid? PostingBatchId { get; private set; }

    public DateTimeOffset? PostingOccurredAt { get; private set; }

    /// <summary>Las líneas que se apartaron antes del ledger (§5.6): una compensación o una reserva nueva tampoco las toca.</summary>
    public IReadOnlyList<Guid> SkippedLineIds => _skippedLineIds;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DocumentStockState Create(Guid tenantId, string documentType, Guid documentId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        return new DocumentStockState
        {
            TenantId = tenantId,
            DocumentType = documentType,
            DocumentId = documentId,
            LastVersion = 0,
            Status = DocumentStockStatus.None,
            UpdatedAt = now,
        };
    }

    /// <summary>Avanza la versión y el estado. La versión nunca retrocede.</summary>
    public void Advance(long version, DocumentStockStatus status, DateTimeOffset now)
    {
        LastVersion = Math.Max(LastVersion, version);
        Status = status;
        UpdatedAt = now;
    }

    /// <summary>Anota dónde y cómo quedó el asiento del documento.</summary>
    public void RecordPosting(Guid warehouseId, Guid locationId, Guid? postingBatchId, DateTimeOffset? postingOccurredAt)
    {
        WarehouseId = warehouseId;
        LocationId = locationId;
        PostingBatchId = postingBatchId;
        PostingOccurredAt = postingOccurredAt;
    }

    public void ClearPosting()
    {
        PostingBatchId = null;
        PostingOccurredAt = null;
    }

    public void SetSkippedLines(IEnumerable<Guid> lineIds)
    {
        ArgumentNullException.ThrowIfNull(lineIds);

        var next = lineIds.Distinct().ToList();
        _skippedLineIds.Clear();
        _skippedLineIds.AddRange(next);
    }
}
