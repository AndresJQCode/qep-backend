using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-22 §8.5: the only implementation of the names a bell entry shows. Notifications does not reference Messaging, so the member
/// names reuse <see cref="IMessagingMemberNames"/> (the rule of «Miembro eliminado» is Messaging's) and the contact names read what
/// Messaging shows for a conversation (<see cref="IMessagingConversationLabels"/>). A contact without a name is not in the result: the
/// screen says «un contacto».
/// </summary>
internal sealed class InAppNotificationNames(IMessagingMemberNames memberNames, IMessagingConversationLabels labels) : IInAppNotificationNames
{
    public Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        memberNames.FindAsync(tenantId, memberIds, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> ConversationContactNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
        labels.ContactNamesAsync(tenantId, conversationIds, cancellationToken);
}
