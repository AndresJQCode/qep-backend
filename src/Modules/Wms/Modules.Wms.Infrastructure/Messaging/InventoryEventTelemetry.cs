using Microsoft.Extensions.Logging;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Messaging;

/// <summary>Log y métrica de lo que el inventario básico aparta o corrige (spec §5.6).</summary>
internal sealed partial class InventoryEventTelemetry(WmsMetrics metrics, ILogger<InventoryEventTelemetry> logger) : IInventoryEventTelemetry
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory line {LineId} of {DocumentType} {DocumentId} (tenant {TenantId}) is not moved: {Reason}.")]
    private static partial void LogLineSkipped(ILogger logger, Guid tenantId, string documentType, Guid documentId, Guid lineId, SkipReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory event of {DocumentType} {DocumentId} (tenant {TenantId}) occurred at {OccurredAt}, outside the ledger window; the processing time is used.")]
    private static partial void LogOutOfWindow(ILogger logger, Guid tenantId, string documentType, Guid documentId, DateTimeOffset occurredAt);

    public void LineSkipped(Guid tenantId, string documentType, Guid documentId, Guid lineId, SkipReason reason)
    {
        metrics.RecordLineSkipped(reason.ToString());
        LogLineSkipped(logger, tenantId, documentType, documentId, lineId, reason);
    }

    public void OccurredAtOutOfWindow(Guid tenantId, string documentType, Guid documentId, DateTimeOffset occurredAt) =>
        LogOutOfWindow(logger, tenantId, documentType, documentId, occurredAt);
}
