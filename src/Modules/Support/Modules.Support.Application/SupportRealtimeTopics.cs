namespace Modules.Support.Application;

/// <summary>Spec 2026-11-15 §5.1: Support's topics on the app hub. <c>support</c> is the tenant's inbox;
/// <c>support:ticket:{id}</c> is one ticket's detail. They are contract with the screens, like the event names.</summary>
public static class SupportRealtimeTopics
{
    public const string Prefix = "support";

    public const string Inbox = "support";

    private const string TicketPrefix = "support:ticket:";

    public static string Ticket(Guid ticketId) => $"{TicketPrefix}{ticketId:D}";

    /// <summary>Exact form only (a <c>D</c>-format Guid): anything else is not a ticket topic, so the hub refuses it.</summary>
    public static bool TryParseTicket(string topic, out Guid ticketId)
    {
        ticketId = Guid.Empty;
        return topic.StartsWith(TicketPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(topic.AsSpan(TicketPrefix.Length), "D", out ticketId);
    }
}

public static class SupportRealtimeEvents
{
    public const string TicketChanged = "supportTicketChanged";
}
