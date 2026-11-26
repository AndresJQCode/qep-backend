using System.Collections.Concurrent;
using System.Text.Json;
using Bootstrapper.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>A wall clock that only moves when the test moves it; it starts at the real time (like <c>ValkeyPresenceTests.PresenceClock</c>).</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>Records what the hub context sends, per target connection, so a test can read what each connection received.</summary>
internal sealed class RecordingHubContext : IHubContext<AppHub>
{
    private readonly ConcurrentQueue<(string ConnectionId, string Method, JsonElement Payload)> _sent = new();

    public RecordingHubContext() => Clients = new RecordingHubClients(this);

    public IHubClients Clients { get; }

    public IGroupManager Groups => AddedGroups;

    /// <summary>The groups the hub joined its connections to, as <c>(connectionId, group)</c>.</summary>
    public RecordingGroups AddedGroups { get; } = new();

    public IReadOnlyList<(string ConnectionId, string Method, JsonElement Payload)> Sent => [.. _sent];

    private void Record(IEnumerable<string> connectionIds, string method, object?[] args)
    {
        var payload = JsonSerializer.SerializeToElement(args.Single(), JsonSerializerOptions.Web);
        foreach (var connectionId in connectionIds)
        {
            _sent.Enqueue((connectionId, method, payload));
        }
    }

    private sealed class RecordingHubClients(RecordingHubContext owner) : IHubClients
    {
        public IClientProxy All => Proxy(["*all"]);

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy(["*all"]);

        public IClientProxy Client(string connectionId) => Proxy([connectionId]);

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy(connectionIds);

        public IClientProxy Group(string groupName) => Proxy(["group:" + groupName]);

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy(["group:" + groupName]);

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy([.. groupNames.Select(name => "group:" + name)]);

        public IClientProxy User(string userId) => Proxy(["user:" + userId]);

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy([.. userIds.Select(id => "user:" + id)]);

        private Proxy Proxy(IReadOnlyList<string> targets) => new(owner, targets);
    }

    private sealed class Proxy(RecordingHubContext owner, IReadOnlyList<string> targets) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            owner.Record(targets, method, args);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingGroups : IGroupManager
    {
        private readonly ConcurrentQueue<(string ConnectionId, string Group)> _added = new();
        private readonly ConcurrentQueue<(string ConnectionId, string Group)> _removed = new();

        public IReadOnlyList<(string ConnectionId, string Group)> Added => [.. _added];

        public IReadOnlyList<(string ConnectionId, string Group)> Removed => [.. _removed];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            _added.Enqueue((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            _removed.Enqueue((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}

/// <summary>Every member is named after its id: the sweeper only needs a name to put in the payload.</summary>
internal sealed class FixedMemberNames : IRealtimeMemberNames
{
    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(memberIds.Distinct().ToDictionary(id => id, id => $"Miembro {id:N}"));
}
