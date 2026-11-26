namespace Modules.Support.Application;

/// <summary>Spec 2026-11-15 §5.1: Support's topics on the app hub. <c>support</c> is the tenant's inbox;
/// <c>support:ticket:{id}</c> is one ticket's detail; <c>support:tasks:{memberId}</c> is one member's own tasks (spec 2026-11-22,
/// plan D1). They are contract with the screens, like the event names.</summary>
public static class SupportRealtimeTopics
{
    public const string Prefix = "support";

    public const string Inbox = "support";

    private const string TicketPrefix = "support:ticket:";

    // Colons and not dots as the spec writes it (plan D1): RealtimeTopics.PrefixOf cuts at the first colon and the hub picks the
    // authorizer by that prefix; a dotted topic would be its own prefix and nobody would authorize it.
    private const string TasksPrefix = "support:tasks:";

    public static string Ticket(Guid ticketId) => $"{TicketPrefix}{ticketId:D}";

    /// <summary>Exact form only: the topic must be what <see cref="Ticket"/> writes, byte for byte. A <c>D</c>-format Guid in
    /// uppercase parses, but it would name another SignalR group and another presence key for the same ticket, splitting the room;
    /// so anything that does not round-trip is not a ticket topic, and the hub refuses it.</summary>
    public static bool TryParseTicket(string topic, out Guid ticketId)
    {
        if (topic.StartsWith(TicketPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(topic.AsSpan(TicketPrefix.Length), "D", out ticketId)
            && string.Equals(Ticket(ticketId), topic, StringComparison.Ordinal))
        {
            return true;
        }

        ticketId = Guid.Empty;
        return false;
    }

    public static string Tasks(Guid memberId) => $"{TasksPrefix}{memberId:D}";

    /// <summary>Exact form only, like <see cref="TryParseTicket"/>: an uppercase or braced Guid would name another SignalR group for the
    /// same member, so anything that does not round-trip is not a tasks topic.</summary>
    public static bool TryParseTasks(string topic, out Guid memberId)
    {
        if (topic.StartsWith(TasksPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(topic.AsSpan(TasksPrefix.Length), "D", out memberId)
            && string.Equals(Tasks(memberId), topic, StringComparison.Ordinal))
        {
            return true;
        }

        memberId = Guid.Empty;
        return false;
    }
}

public static class SupportRealtimeEvents
{
    public const string TicketChanged = "supportTicketChanged";
}
