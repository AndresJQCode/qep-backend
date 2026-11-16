namespace Bootstrapper.Realtime;

/// <summary>
/// Spec 2026-11-15 §5.3: the times of «está respondiendo». The screen renews every 20 s while there is text, so an entry lives
/// <see cref="EntryTtl"/> (two renewals can be lost); the Valkey key expires <see cref="KeyTtl"/> after the last write, so a ticket
/// nobody writes on disappears by itself; and <see cref="SweepInterval"/> bounds a ghost of a dead pod to about 50 s (plan N10).
/// </summary>
internal static class RealtimePresence
{
    public static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    public const string Composing = "composing";

    public const string None = "none";

    /// <summary>
    /// Presence lives on a sub-topic (<c>support:ticket:{id}</c>), never on a module's bare prefix (<c>support</c>, the inbox): a list
    /// is not a room anyone writes in (plan N5, N6). The hub uses it to decide when to send the photo and to announce departures.
    /// </summary>
    public static bool Applies(string topic) => topic.Contains(':', StringComparison.Ordinal);
}

/// <summary>Plan N11: who is composing on a topic, per connection. Each method returns the distinct live members after the change.</summary>
internal interface IRealtimePresence
{
    Task<IReadOnlyList<Guid>> SetComposingAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ClearAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ReadAsync(Guid tenantId, string topic, CancellationToken ct);
}

/// <summary>Plan N12: the visible name of each member (<c>DisplayName ?? email</c>), the rule <c>MessagingMemberNames</c> already owns.
/// A member that is not in the result is shown as «Sin nombre».</summary>
internal interface IRealtimeMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
}
