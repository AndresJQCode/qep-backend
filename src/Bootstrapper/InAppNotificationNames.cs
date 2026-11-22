using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-22 §8.5: the only implementation of the names a bell entry shows. Notifications does not reference Messaging, so the member
/// names reuse <see cref="IMessagingMemberNames"/> (the rule of «Miembro eliminado» is Messaging's) and the contact names will read what
/// Messaging shows for a conversation. Until the Messaging reader exists (plan, task 6) a contact has no name: the screen says «un contacto».
/// </summary>
internal sealed class InAppNotificationNames(IMessagingMemberNames memberNames) : IInAppNotificationNames
{
    public Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        memberNames.FindAsync(tenantId, memberIds, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> ConversationContactNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}
