using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.Logging;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Spec 2026-11-15 §5.2: <c>supportTicketChanged { ticketId }</c> on the ticket's topic and on the inbox. The payload is the id only: the
/// screen rereads over HTTP with its own authorization. It swallows every failure, including one from a publisher that breaks its own
/// contract, because the write that announced already committed; the log carries ids only, never the payload.
/// </summary>
internal sealed partial class SupportRealtimeNotifier(IRealtimePublisher publisher, ILogger<SupportRealtimeNotifier> logger) : ISupportRealtime
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The realtime notice of ticket {TicketId} of tenant {TenantId} could not be published.")]
    private static partial void LogNoticeFailed(ILogger logger, Guid ticketId, Guid tenantId, Exception exception);

    public Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken) =>
        PublishAsync(tenantId, ticketId, [], cancellationToken);

    public Task TaskChangedAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        return PublishAsync(tenantId, ticketId, memberIds, cancellationToken);
    }

    // The ticket's topic, the inbox and one topic per distinct member; one failure stops the rest, as it always did: the write that
    // announced already committed and the screens reread on reconnect.
    private async Task PublishAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var payload = new { ticketId };
        try
        {
            await publisher.PublishAsync(
                tenantId, SupportRealtimeTopics.Ticket(ticketId), SupportRealtimeEvents.TicketChanged, payload, cancellationToken);
            await publisher.PublishAsync(
                tenantId, SupportRealtimeTopics.Inbox, SupportRealtimeEvents.TicketChanged, payload, cancellationToken);
            foreach (var memberId in memberIds.Distinct())
            {
                await publisher.PublishAsync(
                    tenantId, SupportRealtimeTopics.Tasks(memberId), SupportRealtimeEvents.TicketChanged, payload, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            LogNoticeFailed(logger, ticketId, tenantId, exception);
        }
    }
}
