using System.Text.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>Acumula los eventos de Bodegas en la proyección del outbox del <see cref="WmsDbContext"/>, para que commiteen atómicos
/// con el cambio y su auditoría. Nombres en minúscula, como el resto de los payloads del outbox: el consumidor los lee por nombre
/// con <c>JsonDocument</c>.</summary>
internal sealed class WmsEventPublisher(WmsDbContext dbContext) : IWmsEventPublisher
{
    private const string AdjustmentPostedEvent = "wms.adjustment.posted.v1";
    private const string BalanceChangedEvent = "wms.balance.changed.v1";
    private const string ExportReadyEvent = "wms.export-ready.v1";

    public void PublishAdjustmentPosted(Adjustment adjustment, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        Add(
            AdjustmentPostedEvent,
            new AdjustmentPostedPayload(
                adjustment.TenantId,
                adjustment.Id,
                adjustment.Number,
                adjustment.WarehouseId.Value,
                adjustment.OwnerId.Value,
                adjustment.Reason.ToString(),
                adjustment.BatchId ?? throw new InvalidOperationException("A posted adjustment has a batch."),
                adjustment.Lines.Count,
                adjustment.PostedAt ?? occurredAt),
            occurredAt);
    }

    public void PublishBalanceChanged(BalanceChangedPayload payload, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Add(
            BalanceChangedEvent,
            new BalanceChangedBody(
                payload.TenantId,
                payload.OwnerId,
                payload.WarehouseId,
                payload.VariantId,
                payload.OnHand,
                payload.Reserved,
                payload.Available,
                payload.ChangedAt),
            occurredAt);
    }

    public void PublishExportReady(
        Guid tenantId,
        Guid subjectId,
        Guid fileId,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt) =>
        Add(ExportReadyEvent, new ExportReadyPayload(tenantId, subjectId, fileId, downloadUrl, fileName, rowCount, expiresAt), occurredAt);

    private void Add<TPayload>(string eventName, TPayload payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new WmsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });

    private sealed record AdjustmentPostedPayload(
        Guid tenantId,
        Guid adjustmentId,
        string number,
        Guid warehouseId,
        Guid ownerId,
        string reason,
        Guid batchId,
        int lineCount,
        DateTimeOffset postedAt);

    private sealed record BalanceChangedBody(
        Guid tenantId,
        Guid ownerId,
        Guid warehouseId,
        Guid variantId,
        decimal onHand,
        decimal reserved,
        decimal available,
        DateTimeOffset changedAt);

    private sealed record ExportReadyPayload(
        Guid tenantId,
        Guid subjectId,
        Guid fileId,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt);
}
