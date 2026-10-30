using System.Text.Json;

namespace Modules.Messaging.Infrastructure.Coexistence;

/// <summary>El payload del evento de coexistencia lo escribe <c>IntegrationsEventPublisher</c> y se lee por nombre (camelCase):
/// <c>tenantId</c>, <c>connectionId</c>, <c>occurredAt</c>. Messaging no referencia Integrations, así que la pareja la amarra
/// <c>CoexistenceEventParityTests</c>. Un payload roto es determinista: reintentarlo no lo arregla, así que se lee como inválido y
/// el mensaje se marca procesado.</summary>
internal static class CoexistenceEventPayload
{
    public static bool TryRead(string json, out Guid tenantId, out Guid connectionId, out DateTimeOffset occurredAt)
    {
        tenantId = connectionId = Guid.Empty;
        occurredAt = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            tenantId = root.GetProperty("tenantId").GetGuid();
            connectionId = root.GetProperty("connectionId").GetGuid();
            occurredAt = root.GetProperty("occurredAt").GetDateTimeOffset();
            return tenantId != Guid.Empty && connectionId != Guid.Empty;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
