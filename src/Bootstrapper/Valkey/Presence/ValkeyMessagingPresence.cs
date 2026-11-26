using Modules.Messaging.Application;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// El <see cref="IMessagingPresence"/> de <c>MessagingHub</c> con <c>Valkey:Areas:Presence</c> prendido (spec 2026-11-13,
/// D-2). El registro local sigue siendo la fuente del pod —de él salen el latido y el respaldo del lector—, y además cada
/// cambio sale de inmediato a <c>qep:presence:{tenantId}</c> para que «Conectados» no espere al siguiente latido.
///
/// Las escrituras no se esperan: el hub no puede quedar colgado de Valkey. Si fallan, el gateway cuenta el respaldo y el
/// latido de <see cref="PresenceHeartbeatWorker"/> corrige en ≤ 30 s. Al desconectar sólo se quita al miembro si este pod no
/// le tiene otra pestaña abierta; si sigue conectado por otro pod, el latido de ese pod lo vuelve a poner.
/// </summary>
internal sealed class ValkeyMessagingPresence(MessagingPresence local, ValkeyGateway gateway, TimeProvider time) : IMessagingPresence
{
    public void Connected(string connectionId, Guid tenantId, Guid memberId)
    {
        local.Connected(connectionId, tenantId, memberId);
        var key = ValkeyKeys.Presence(tenantId);
        var score = ValkeyPresence.ScoreOf(time.GetUtcNow());
        _ = gateway.TryAsync(
            ValkeyArea.Presence,
            async database =>
            {
                var add = database.SortedSetAddAsync(key, memberId.ToString("D"), score);
                var expire = database.KeyExpireAsync(key, ValkeyPresence.KeyTtl);
                await Task.WhenAll(add, expire).ConfigureAwait(false);
                return true;
            },
            CancellationToken.None);
    }

    public void Disconnected(string connectionId)
    {
        if (!local.TryRemove(connectionId, out var tenantId, out var memberId) || local.HasConnection(tenantId, memberId))
        {
            return;
        }

        var key = ValkeyKeys.Presence(tenantId);
        _ = gateway.TryAsync(
            ValkeyArea.Presence,
            database => database.SortedSetRemoveAsync(key, memberId.ToString("D")),
            CancellationToken.None);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot() => local.Snapshot();
}
