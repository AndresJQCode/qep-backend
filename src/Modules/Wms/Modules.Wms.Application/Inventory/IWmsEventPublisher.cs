using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

/// <summary>Lo que cambió en el saldo de una variante, agregado por dueño y bodega: el cuerpo de <c>wms.balance.changed.v1</c>.</summary>
public sealed record BalanceChangedPayload(
    Guid TenantId,
    Guid OwnerId,
    Guid WarehouseId,
    Guid VariantId,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    DateTimeOffset ChangedAt);

/// <summary>Los eventos de Bodegas hacia el resto del sistema (S19). Acumulan en la proyección del outbox del <c>WmsDbContext</c> sin
/// guardar: los commitea el <c>SaveChangesAsync</c> del handler, junto con el cambio y su auditoría.</summary>
public interface IWmsEventPublisher
{
    void PublishAdjustmentPosted(Adjustment adjustment, DateTimeOffset occurredAt);

    void PublishBalanceChanged(BalanceChangedPayload payload, DateTimeOffset occurredAt);

    void PublishExportReady(
        Guid tenantId,
        Guid subjectId,
        Guid fileId,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt);
}

/// <summary><c>WmsMetrics</c> es interno de Infrastructure: los handlers que confirman un documento contra el ledger miden por este
/// puerto (RNF-07, <c>qep.wms.confirm.duration</c>).</summary>
public interface IWmsConfirmMetrics
{
    void RecordConfirmDuration(double milliseconds);
}
