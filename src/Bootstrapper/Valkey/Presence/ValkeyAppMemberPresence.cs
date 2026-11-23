using Bootstrapper.Realtime;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// The <see cref="IAppMemberPresence"/> with <c>Valkey:Areas:Presence</c> on (spec 2026-11-22 D-1): every change goes at once to
/// <c>qep:rt:online:{tenantId}</c> so another pod's worker sees the member without waiting for a heartbeat, and
/// <see cref="AppPresenceHeartbeatWorker"/> keeps it alive. Writes are not awaited: the hub never hangs on Valkey, and a lost write is
/// corrected by the next heartbeat. Closing a tab removes the member only when this pod has no other tab of them; if another pod still
/// has one, that pod's heartbeat puts them back. A read that fails answers from this pod's registry, which can only say «connected» for
/// the pod's own members; the worker treats the rest as disconnected and escalates, which is the safe side.
/// </summary>
internal sealed class ValkeyAppMemberPresence(
    LocalAppMemberPresence local,
    AppHubConnections connections,
    ValkeyGateway gateway,
    TimeProvider time) : IAppMemberPresence
{
    public void Connected(Guid tenantId, Guid memberId)
    {
        var key = ValkeyKeys.AppOnline(tenantId);
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

    public void Disconnected(Guid tenantId, Guid memberId)
    {
        if (connections.HasMember(tenantId, memberId))
        {
            return;
        }

        var key = ValkeyKeys.AppOnline(tenantId);
        _ = gateway.TryAsync(
            ValkeyArea.Presence,
            database => database.SortedSetRemoveAsync(key, memberId.ToString("D")),
            CancellationToken.None);
    }

    public async Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var key = ValkeyKeys.AppOnline(tenantId);
        var cutoff = ValkeyPresence.CutoffOf(time.GetUtcNow());
        var attempt = await gateway.TryAsync(
            ValkeyArea.Presence,
            database => database.SortedSetScoreAsync(key, memberId.ToString("D")),
            cancellationToken).ConfigureAwait(false);

        return attempt.Succeeded
            ? attempt.Value is { } score && score > cutoff
            : await local.IsConnectedAsync(tenantId, memberId, cancellationToken).ConfigureAwait(false);
    }
}
