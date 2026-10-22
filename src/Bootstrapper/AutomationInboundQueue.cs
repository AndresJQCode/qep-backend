using Modules.Automations.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-21 §5.1 y §5.3: la cola del motor es <see cref="IAutomationInbox"/> de Mensajería, reexpuesta con tipos de Automations
/// (que no referencia Messaging). El testigo <c>attempts</c> de Complete y RetryLater pasa tal cual (P2-R3): la cerca contra un lease
/// vencido la hace el UPDATE de Mensajería.
/// </summary>
internal sealed class AutomationInboundQueue(IAutomationInbox inbox) : IAutomationInboundQueue
{
    public async Task<IReadOnlyList<InboundQueueItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken) =>
    [
        .. (await inbox.ClaimAsync(now, batch, cancellationToken)).Select(item => new InboundQueueItem(
            item.MessageId, item.TenantId, item.ConnectionId, item.ConversationId, item.Opened, item.CreatedAt, item.Attempts)),
    ];

    public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken) =>
        inbox.CompleteAsync(tenantId, messageId, attempts, outcome, cancellationToken);

    public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken) =>
        inbox.RetryLaterAsync(tenantId, messageId, attempts, delay, cancellationToken);

    /// <summary><c>Kind</c> viaja como el nombre del <c>MessageKind</c> (<c>CustomerMessageKinds</c> del motor); el texto ya trae la
    /// leyenda cuando el entrante no tiene texto (P2-R4, en <c>ReadInboundAsync</c>).</summary>
    public async Task<InboundMessage?> ReadAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        await inbox.ReadInboundAsync(tenantId, messageId, cancellationToken) is { } inbound
            ? new InboundMessage(
                inbound.MessageId,
                inbound.Kind.ToString(),
                inbound.Text,
                inbound.ReplyId,
                inbound.Media is { } media ? new InboundAttachment(media.MimeType, media.FileName, media.Stored) : null,
                inbound.OccurredAt)
            : null;
}
