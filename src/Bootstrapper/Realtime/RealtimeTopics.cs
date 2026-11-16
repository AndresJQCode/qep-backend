using Modules.Support.Domain;

namespace Bootstrapper.Realtime;

/// <summary>Spec 2026-11-15 §5.1 and plan N5: topic rules of the app hub that do not depend on any module.</summary>
internal static class RealtimeTopics
{
    public const int MaxLength = 200;

    /// <summary>Plan N5: a broken client cannot fill the pod's memory with subscriptions.</summary>
    public const int MaxPerConnection = 50;

    /// <summary>Owner O3: the code for a topic no authorizer claims, a malformed topic or the cap. Support's code until a second module
    /// uses the hub; then it becomes a generic <c>realtime.topic_forbidden</c>.</summary>
    public const string ForbiddenCode = SupportErrorCodes.RealtimeTopicForbidden;

    /// <summary>The tenant goes into the group, so the same topic text in two tenants never shares one.</summary>
    public static string GroupOf(Guid tenantId, string topic) => $"{tenantId:D}:{topic}";

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
