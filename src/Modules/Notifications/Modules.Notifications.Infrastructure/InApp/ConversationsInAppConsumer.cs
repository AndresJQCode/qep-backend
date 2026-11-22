using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 §6 and §12: a conversation that was handed to a member (a transfer, a heritage or an assistant's handoff) reaches that
/// member's bell. Messaging decides when (<c>ConversationAssignmentNotice.From</c>) and writes <c>messaging.conversation.assigned.v1</c>
/// in the same transaction as the change; this only turns it into an entry. The entry keeps the ids: the actor's and the contact's names
/// are resolved when it is read, so a renamed member or customer shows updated.
/// </summary>
internal sealed class ConversationsInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<ConversationsInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    private const string AssignedEvent = "messaging.conversation.assigned.v1";

    protected override string Consumer => "notifications.in-app.conversations";

    protected override IReadOnlyCollection<string> EventNames => [AssignedEvent];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var conversationId = root.GetProperty("conversationId").GetGuid();
        var memberId = root.GetProperty("memberId").GetGuid();
        var reason = root.GetProperty("reason").GetString() ?? string.Empty;
        // null for a heritage or a handoff: nobody acted.
        var actorMemberId = root.TryGetProperty("actorMemberId", out var actor) && actor.ValueKind == JsonValueKind.String
            ? actor.GetGuid()
            : (Guid?)null;

        var data = JsonSerializer.Serialize(new { conversationId, reason, actorMemberId });
        IReadOnlyList<InAppNotification> entries =
        [
            InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.ConversationAssigned,
                resourceId: conversationId,
                data,
                dedupeKey: record.Id.ToString("D"),
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow),
        ];
        return Task.FromResult(entries);
    }
}
