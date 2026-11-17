namespace Modules.Messaging.Application;

/// <summary>
/// Lo que lee el reporte de uso de la plataforma: quién está conectado a la bandeja (spec 2026-11-13, D-2). Con la presencia
/// de Valkey prendida ve a los conectados de todos los pods; si no, o si Valkey falla, los de este proceso.
/// </summary>
public interface IMessagingPresenceReader
{
    /// <summary>Tenant → miembros conectados (sin repetir), sólo de los tenants pedidos. Un tenant sin nadie no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListConnectedAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
}

/// <summary>Lee el registro local del proceso: la implementación con el área apagada y el respaldo del lector de Valkey.</summary>
public sealed class LocalMessagingPresenceReader(IMessagingPresence presence) : IMessagingPresenceReader
{
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListConnectedAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        var requested = tenantIds as ISet<Guid> ?? tenantIds.ToHashSet();
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> connected = presence.Snapshot()
            .Where(entry => requested.Contains(entry.Key) && entry.Value.Count > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        return Task.FromResult(connected);
    }
}
