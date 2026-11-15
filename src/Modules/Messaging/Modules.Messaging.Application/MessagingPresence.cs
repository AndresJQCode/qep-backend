using System.Collections.Concurrent;

namespace Modules.Messaging.Application;

/// <summary>
/// Quién tiene abierta una conexión de la bandeja en este momento. Lo alimenta <c>MessagingHub</c>. El registro local es
/// por proceso; lo que lee el reporte de uso pasa por <see cref="IMessagingPresenceReader"/>, que con Valkey ve a los
/// conectados de todos los pods y sin él cae a este registro (spec 2026-11-13, D-2).
/// </summary>
public interface IMessagingPresence
{
    void Connected(string connectionId, Guid tenantId, Guid memberId);

    /// <summary>Un id desconocido no hace nada: el hub puede cerrar una conexión que nunca llegó a registrarse.</summary>
    void Disconnected(string connectionId);

    /// <summary>Tenant → miembros conectados a este proceso, sin repetir aunque tengan varias pestañas abiertas.</summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot();
}

/// <summary>El registro local, por proceso: el de siempre con un solo pod y el respaldo cuando la presencia compartida falla.</summary>
public sealed class MessagingPresence : IMessagingPresence
{
    private readonly ConcurrentDictionary<string, (Guid TenantId, Guid MemberId)> _connections = new();

    public void Connected(string connectionId, Guid tenantId, Guid memberId) =>
        _connections[connectionId] = (tenantId, memberId);

    public void Disconnected(string connectionId) => TryRemove(connectionId, out _, out _);

    /// <summary>Quita la conexión y dice de quién era; <c>false</c> con un id desconocido.</summary>
    public bool TryRemove(string connectionId, out Guid tenantId, out Guid memberId)
    {
        if (_connections.TryRemove(connectionId, out var owner))
        {
            (tenantId, memberId) = owner;
            return true;
        }

        tenantId = Guid.Empty;
        memberId = Guid.Empty;
        return false;
    }

    /// <summary>Si el miembro tiene otra conexión abierta en este proceso (otra pestaña).</summary>
    public bool HasConnection(Guid tenantId, Guid memberId) =>
        _connections.Values.Any(connection => connection.TenantId == tenantId && connection.MemberId == memberId);

    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot() =>
        _connections.Values
            .GroupBy(connection => connection.TenantId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(connection => connection.MemberId).Distinct().ToList());
}
