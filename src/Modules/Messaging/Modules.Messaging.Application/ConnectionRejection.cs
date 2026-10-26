using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// §8.3: Meta rechazó la credencial o el número (190 / 133010). Extraído de <see cref="OutboundSettlement"/> sin cambiar su comportamiento
/// para que el bloqueo de un contacto (spec 2026-10-26 §3.3) pase la conexión a NeedsAttention igual que un envío.
/// </summary>
internal static class ConnectionRejection
{
    // Si Integrations no logra pasar la conexión a NeedsAttention, la respuesta sigue siendo el 422 documentado;
    // la causa viaja como InnerException y ApiExceptionHandler la deja en platform.request_failures (esta clase es estática
    // y no recibe logger). El próximo intento vuelve a recibir 190/133010 y reintenta el reporte.
    public static async Task ReportAsync(IMessagingConnectionDirectory connections, Guid tenantId, Guid connectionId, int code)
    {
        ArgumentNullException.ThrowIfNull(connections);
        try
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, MetaErrorCodes.RejectedReason(code), CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials; the connection state could not be updated.", exception);
        }
    }
}
