namespace Bootstrapper.Realtime;

/// <summary>Spec 2026-11-15 §5.1 and plan N5: topic rules of the app hub that do not depend on any module.</summary>
internal static class RealtimeTopics
{
    public const int MaxLength = 200;

    /// <summary>Plan N5: a broken client cannot fill the pod's memory with subscriptions.</summary>
    public const int MaxPerConnection = 50;

    /// <summary>Spec 2026-11-22 D-8: the code for a topic no authorizer claims, a malformed topic or the cap. It was Support's code until a
    /// second module (Messaging) joined the hub; each authorizer keeps its own code for what it refuses.</summary>
    public const string ForbiddenCode = "realtime.topic_forbidden";

    /// <summary>The topic text of the envelopes that carry a <see cref="RealtimeEnvelope.MemberId"/>. It starts with <c>@</c>, which no
    /// authorizer prefix may, so a client can never <c>Subscribe</c> to it.</summary>
    public const string MemberTopic = "@member";

    /// <summary>The tenant goes into the group, so the same topic text in two tenants never shares one.</summary>
    public static string GroupOf(Guid tenantId, string topic) => $"{tenantId:D}:{topic}";

    /// <summary>The group the hub joins every connection of the member to, built from the connection's claims, never from the client.</summary>
    public static string MemberGroupOf(Guid tenantId, Guid memberId) => $"{tenantId:D}:{MemberTopic}:{memberId:D}";

    public static string PrefixOf(string topic)
    {
        var colon = topic.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? topic : topic[..colon];
    }
}

internal static class AppHubEvents
{
    public const string PresenceChanged = "presenceChanged";
}
