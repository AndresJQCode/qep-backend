using System.Text.Json.Nodes;

namespace Modules.Notifications.Domain;

/// <summary>
/// Spec 2026-11-22 D-4: one entry of a member's bell. It is not a <see cref="Notification"/>: that one records a <b>delivery</b>
/// (status, address, anonymization) and this one is what a person reads, so they share no invariant. It keeps ids, immutable numbers
/// and a <see cref="Kind"/>, never rendered text; names are resolved when it is read.
/// </summary>
public sealed class InAppNotification
{
    /// <summary>Column length of <see cref="DedupeKey"/>; the longest key is <c>support-unassigned:{ticket}:{since:O}</c>.</summary>
    public const int DedupeKeyMaxLength = 200;

    private InAppNotification()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The recipient membership, never a user: a person can belong to several tenants.</summary>
    public Guid MemberId { get; private set; }

    public InAppNotificationKind Kind { get; private set; }

    /// <summary>The conversation, ticket or order the entry is about, to resolve and deduplicate it.</summary>
    public Guid? ResourceId { get; private set; }

    /// <summary>Spec §8.5 without the names resolved on read (it keeps <c>actorMemberId</c>, not <c>actorName</c>).</summary>
    public string DataJson { get; private set; } = "{}";

    /// <summary>With <see cref="TenantId"/> and <see cref="MemberId"/> it is unique: a repeated event writes nothing twice (D-7).</summary>
    public string DedupeKey { get; private set; } = string.Empty;

    public Guid? SourceMessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public static InAppNotification Create(
        Guid tenantId,
        Guid memberId,
        InAppNotificationKind kind,
        Guid? resourceId,
        string dataJson,
        string dedupeKey,
        Guid? sourceMessageId,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MemberId = memberId,
            Kind = kind,
            ResourceId = resourceId,
            DataJson = dataJson,
            DedupeKey = dedupeKey,
            SourceMessageId = sourceMessageId,
            CreatedAt = createdAt,
        };

    /// <returns><c>false</c> when it was already read: the first <see cref="ReadAt"/> stays.</returns>
    public bool MarkRead(DateTimeOffset now)
    {
        if (ReadAt is not null)
        {
            return false;
        }

        ReadAt = now;
        return true;
    }

    /// <summary>Closes an entry whose subject was decided elsewhere (a wholesale order approved or cancelled): it is read from then on.</summary>
    /// <returns><c>false</c>, changing nothing, when it was already resolved: the first resolution stays (D-13).</returns>
    public bool Resolve(string resolution, DateTimeOffset now)
    {
        if (ResolvedAt is not null)
        {
            return false;
        }

        var data = JsonNode.Parse(DataJson) as JsonObject ?? [];
        data["resolution"] = resolution;
        DataJson = data.ToJsonString();
        ResolvedAt = now;
        ReadAt ??= now;
        return true;
    }

    // Never the data: it can hold file names and links (spec §13).
    public override string ToString() => $"InAppNotification {Id} ({Kind}) of member {MemberId}";
}
