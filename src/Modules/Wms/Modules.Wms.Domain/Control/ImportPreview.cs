using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>La vista previa de una carga inicial: las filas ya resueltas, guardadas hasta que alguien las confirma o vencen.</summary>
public sealed class ImportPreview
{
    private ImportPreview()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RequestedBy { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    public OwnerId OwnerId { get; private set; }

    public string RowsJson { get; private set; } = "[]";

    public bool CanCommit { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public static ImportPreview Create(
        Guid tenantId,
        Guid requestedBy,
        WarehouseId warehouseId,
        OwnerId ownerId,
        string rowsJson,
        bool canCommit,
        DateTimeOffset now,
        TimeSpan lifetime) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RequestedBy = requestedBy,
            WarehouseId = warehouseId,
            OwnerId = ownerId,
            RowsJson = rowsJson,
            CanCommit = canCommit,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;
}
