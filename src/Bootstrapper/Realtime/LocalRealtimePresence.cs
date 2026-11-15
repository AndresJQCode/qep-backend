using System.Collections.Concurrent;

namespace Bootstrapper.Realtime;

/// <summary>
/// Plan N11: the presence of this pod, in memory. It is the registry when <c>Valkey:Areas:Presence</c> is off, and the fallback when
/// Valkey fails (spec §5.3 accepts the degradation: with several replicas a notice may not cross pods, and <c>If-Match</c> remains the
/// last net). Keyed by connection, so two tabs of one member never step on each other; expired entries are dropped on every access.
/// </summary>
internal sealed class LocalRealtimePresence(TimeProvider time) : IRealtimePresence
{
    private readonly ConcurrentDictionary<(Guid TenantId, string Topic), Dictionary<string, Entry>> _rooms = new();

    public Task<IReadOnlyList<Guid>> SetComposingAsync(
        Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct)
    {
        while (true)
        {
            var room = _rooms.GetOrAdd((tenantId, topic), _ => new Dictionary<string, Entry>(StringComparer.Ordinal));
            lock (room)
            {
                // A room emptied (and removed) by another call between GetOrAdd and the lock is an orphan: write in the current one.
                if (!_rooms.TryGetValue((tenantId, topic), out var current) || !ReferenceEquals(current, room))
                {
                    continue;
                }

                room[connectionId] = new Entry(memberId, time.GetUtcNow() + RealtimePresence.EntryTtl);
                return Task.FromResult(LiveMembers(tenantId, topic, room));
            }
        }
    }

    public Task<IReadOnlyList<Guid>> ClearAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct)
    {
        if (!_rooms.TryGetValue((tenantId, topic), out var room))
        {
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        lock (room)
        {
            room.Remove(connectionId);
            return Task.FromResult(LiveMembers(tenantId, topic, room));
        }
    }

    public Task<IReadOnlyList<Guid>> ReadAsync(Guid tenantId, string topic, CancellationToken ct)
    {
        if (!_rooms.TryGetValue((tenantId, topic), out var room))
        {
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        lock (room)
        {
            return Task.FromResult(LiveMembers(tenantId, topic, room));
        }
    }

    // Called with the room locked. An empty room leaves the dictionary so the registry does not grow with every ticket ever opened.
    private IReadOnlyList<Guid> LiveMembers(Guid tenantId, string topic, Dictionary<string, Entry> room)
    {
        var now = time.GetUtcNow();
        foreach (var expired in room.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
        {
            room.Remove(expired);
        }

        if (room.Count == 0)
        {
            _rooms.TryRemove(new KeyValuePair<(Guid, string), Dictionary<string, Entry>>((tenantId, topic), room));
            return [];
        }

        return [.. room.Values.Select(entry => entry.MemberId).Distinct()];
    }

    private sealed record Entry(Guid MemberId, DateTimeOffset ExpiresAt);
}
