using System.Collections.Concurrent;

namespace Modules.Messaging.Application;

/// <summary>
/// Quién tiene abierta una conexión de la bandeja en este momento. Lo alimenta <c>MessagingHub</c> y lo lee el reporte de
/// uso de la plataforma. Es por proceso: no hay respaldo en base, así que sólo vale mientras la API corra con una réplica.
/// </summary>
public interface IMessagingPresence
{
    void Connected(string connectionId, Guid tenantId, Guid memberId);

    /// <summary>Un id desconocido no hace nada: el hub puede cerrar una conexión que nunca llegó a registrarse.</summary>
    void Disconnected(string connectionId);

    /// <summary>Tenant → miembros conectados, sin repetir aunque tengan varias pestañas abiertas.</summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot();
}

public sealed class MessagingPresence : IMessagingPresence
{
    private readonly ConcurrentDictionary<string, (Guid TenantId, Guid MemberId)> _connections = new();

    public void Connected(string connectionId, Guid tenantId, Guid memberId) =>
        _connections[connectionId] = (tenantId, memberId);

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot() =>
        _connections.Values
            .GroupBy(connection => connection.TenantId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(connection => connection.MemberId).Distinct().ToList());
}
