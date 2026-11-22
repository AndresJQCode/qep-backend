using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.Logging;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Spec 2026-11-15 §5.2: <c>supportTicketChanged { ticketId }</c> on the ticket's topic and on the inbox. The payload is the id only: the
/// screen rereads over HTTP with its own authorization. It swallows every failure (each publish on its own, so one failing topic does not stop the rest), including one from a publisher that
/// breaks its own contract, because the write that announced already committed; the log carries ids only, never the payload.
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

    // One topic per distinct member first, then the ticket's topic and the inbox. A task-only member listens to nothing else, so the
    // topics that reach her go out before the ones she cannot hear. Each publish is its own try: one failure never stops the rest,
    // because the write that announced already committed and the screens reread on reconnect.
    private async Task PublishAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var payload = new { ticketId };
        foreach (var memberId in memberIds.Distinct())
        {
            await TryPublishAsync(tenantId, ticketId, SupportRealtimeTopics.Tasks(memberId), payload, cancellationToken);
        }

        await TryPublishAsync(tenantId, ticketId, SupportRealtimeTopics.Ticket(ticketId), payload, cancellationToken);
        await TryPublishAsync(tenantId, ticketId, SupportRealtimeTopics.Inbox, payload, cancellationToken);
    }

    private async Task TryPublishAsync(Guid tenantId, Guid ticketId, string topic, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(tenantId, topic, SupportRealtimeEvents.TicketChanged, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            LogNoticeFailed(logger, ticketId, tenantId, exception);
        }
    }
}
