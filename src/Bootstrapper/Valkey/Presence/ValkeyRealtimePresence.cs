using Bootstrapper.Realtime;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// Spec 2026-11-15 §5.3 (plan N11): ticket presence shared between pods with <c>Valkey:Areas:Presence</c> on. One sorted set per topic,
/// <c>qep:rt:presence:{tenantId}:{topic}</c>; the member is <c>{memberId}:{connectionId}</c> and the score the Unix second it expires.
/// Every write renews the key's <c>EXPIRE</c>. Reading trims what expired and then reads the rest, two plain commands because the
/// production ACL has no Lua (<c>-@scripting</c>).
///
/// Unlike <c>ValkeyMessagingPresence</c> the writes are awaited: they return the list the hub publishes, and <see cref="ValkeyGateway"/>
/// already bounds them with <c>OperationTimeoutMs</c>. The local registry is written too, so a failure answers from it (and the gateway
/// counts the fallback of the area).
/// </summary>
internal sealed class ValkeyRealtimePresence(ValkeyGateway gateway, LocalRealtimePresence local, TimeProvider time) : IRealtimePresence
{
    public async Task<IReadOnlyList<Guid>> SetComposingAsync(
        Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct)
    {
        var fallback = await local.SetComposingAsync(tenantId, topic, memberId, connectionId, ct).ConfigureAwait(false);
        var key = ValkeyKeys.RealtimePresence(tenantId, topic);
        var now = time.GetUtcNow();
        var attempt = await gateway.TryAsync(
            ValkeyArea.Presence,
            async database =>
            {
                await database.SortedSetAddAsync(key, EntryOf(memberId, connectionId), ScoreOf(now + RealtimePresence.EntryTtl))
                    .ConfigureAwait(false);
                await database.KeyExpireAsync(key, RealtimePresence.KeyTtl).ConfigureAwait(false);
                return await ReadLiveAsync(database, key, now).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return attempt is { Succeeded: true, Value: { } members } ? members : fallback;
    }

    public async Task<IReadOnlyList<Guid>> ClearAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct)
    {
        var fallback = await local.ClearAsync(tenantId, topic, memberId, connectionId, ct).ConfigureAwait(false);
        var key = ValkeyKeys.RealtimePresence(tenantId, topic);
        var now = time.GetUtcNow();
        var attempt = await gateway.TryAsync(
            ValkeyArea.Presence,
            async database =>
            {
                await database.SortedSetRemoveAsync(key, EntryOf(memberId, connectionId)).ConfigureAwait(false);
                return await ReadLiveAsync(database, key, now).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return attempt is { Succeeded: true, Value: { } members } ? members : fallback;
    }

    public async Task<IReadOnlyList<Guid>> ReadAsync(Guid tenantId, string topic, CancellationToken ct)
    {
        var key = ValkeyKeys.RealtimePresence(tenantId, topic);
        var now = time.GetUtcNow();
        var attempt = await gateway.TryAsync(ValkeyArea.Presence, database => ReadLiveAsync(database, key, now), ct).ConfigureAwait(false);
        return attempt is { Succeeded: true, Value: { } members }
            ? members
            : await local.ReadAsync(tenantId, topic, ct).ConfigureAwait(false);
    }

    private static RedisValue EntryOf(Guid memberId, string connectionId) => $"{memberId:D}:{connectionId}";

    private static double ScoreOf(DateTimeOffset expiresAt) => expiresAt.ToUnixTimeSeconds();

    // An entry whose expiry is now or earlier is gone; a member counts once however many connections it has.
    private static async Task<IReadOnlyList<Guid>> ReadLiveAsync(IDatabase database, RedisKey key, DateTimeOffset now)
    {
        var cutoff = ScoreOf(now);
        await database.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, cutoff).ConfigureAwait(false);
        var entries = await database.SortedSetRangeByScoreAsync(key, cutoff, double.PositiveInfinity, Exclude.Start).ConfigureAwait(false);
        return
        [
            .. entries
                .Select(entry => (string?)entry)
                .Select(entry => entry is { Length: > 36 } && Guid.TryParseExact(entry.AsSpan(0, 36), "D", out var memberId)
                    ? memberId
                    : Guid.Empty)
                .Where(memberId => memberId != Guid.Empty)
                .Distinct(),
        ];
    }
}
