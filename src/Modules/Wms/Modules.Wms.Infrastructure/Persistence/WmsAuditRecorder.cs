using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019): la entrada se acumula en WmsDbContext y commitea o revierte con el cambio. A diferencia de
// los recorders que guardan `[]`, changed_fields viaja como objeto JSON (S23, P-7): el reporte de auditoría de bodegas necesita
// saber qué campo cambió, y un arreglo vacío no lo dice.
internal sealed class WmsAuditRecorder(WmsDbContext dbContext) : IWmsAuditRecorder
{
    private const string Source = "wms";
    private const string Outcome = "success";

    public void Record(
        Guid tenantId,
        Guid actorId,
        WmsActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyDictionary<string, string?> changedFields,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(changedFields);

        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            Map(actorType),
            action,
            resourceType,
            resourceId,
            Outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
    }

    private static AuditActorType Map(WmsActorType actorType) => actorType switch
    {
        WmsActorType.Human => AuditActorType.Human,
        WmsActorType.System => AuditActorType.System,
        WmsActorType.ApiKey => AuditActorType.Integration,
        _ => throw new ArgumentOutOfRangeException(nameof(actorType), actorType, null),
    };
}
