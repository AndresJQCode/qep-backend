namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-22 §8.5: how Messaging names the contact of a conversation, for a bell entry that points at it. Messaging
/// has no reader by ids that does only this, and the notification names are resolved when read (never stored).</summary>
public interface IMessagingConversationLabels
{
    /// <summary>The linked customer's name or, failing that, the contact's profile name. A conversation of another tenant, without
    /// either name, or that no longer exists is not in the result: the screen then says «un contacto».</summary>
    Task<IReadOnlyDictionary<Guid, string>> ContactNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);
}
