using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08, «Eventos de outbox»: para que un consumidor suelte una conexión que ya no sirve.
/// Sólo <see cref="WhatsAppCoexistenceConnected"/> tiene consumidor: Messaging, que importa el historial
/// de la app de WhatsApp Business del número recién conectado.
/// </summary>
public static class ConnectionEvents
{
    public const string Paused = "integrations.connection-paused.v1";
    public const string Deleted = "integrations.connection-deleted.v1";
    public const string NeedsAttention = "integrations.connection-needs-attention.v1";

    /// <summary>Se publica al crear una conexión por coexistencia; <c>occurredAt</c> es la hora de la conexión.</summary>
    public const string WhatsAppCoexistenceConnected = "integrations.whatsapp-coexistence-connected.v1";
}

/// <summary>
/// Escribe el evento en <c>platform.outbox_messages</c> en la misma transacción que el cambio. Puerto
/// propio y no el <c>IOutboxWriter</c> de Tenancy, que está ligado a <c>TenancyDbContext</c> (P2).
/// Payload: <c>{ tenantId, connectionId, providerKey, occurredAt, failureCode }</c>; nunca un valor de campo.
/// <c>failureCode</c> es el código de la conexión al publicar, <c>null</c> si no está en atención.
/// </summary>
public interface IConnectionEventPublisher
{
    void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt);
}

internal static class ConnectionTransitions
{
    /// <summary>
    /// Paso a <c>NeedsAttention</c> con su auditoría y su evento. Lo comparten <c>test</c> (desde
    /// <c>Active</c>) y <c>resume</c> (desde <c>Paused</c>). El dominio no guarda el estado de origen:
    /// quien llama decide si corresponde.
    /// </summary>
    public static void FlagNeedsAttention(
        IIntegrationsAuditRecorder auditRecorder,
        IConnectionEventPublisher eventPublisher,
        IExecutionContext executionContext,
        IntegrationConnection connection,
        ConnectionTestResult result,
        DateTimeOffset now)
    {
        connection.MarkNeedsAttention(ConnectionVerification.FailureCode(result), now);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.NeedsAttention, [], now);
        eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
    }
}
