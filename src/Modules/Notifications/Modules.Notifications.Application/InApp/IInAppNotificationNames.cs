namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8.5: the names a bell entry shows are resolved when it is read, never stored, so a renamed member shows updated.
/// A single implementation lives in Bootstrapper (Notifications does not reference Messaging): the member names reuse Messaging's rule
/// and the conversation labels read what Messaging shows for a contact. What is not in the result is shown by the screen as «un
/// contacto» or left without a name.
/// </summary>
public interface IInAppNotificationNames
{
    Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>The linked customer's name or, failing that, the contact's profile name; a conversation with neither is not in the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ConversationContactNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);
}
