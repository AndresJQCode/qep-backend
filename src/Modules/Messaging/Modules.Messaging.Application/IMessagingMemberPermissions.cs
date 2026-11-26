namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-22 §9.2: whether a member holds any of <paramref name="permissions"/> right now, read from the database and not from
/// claims. The app hub asks long after the handshake (a <c>Subscribe</c> hours later, or an alert for a member already connected must see
/// today's roles) and it only knows the member id. A copy of <c>ISupportMemberPermissions</c>, as its own port so neither module
/// references the other.
/// </summary>
public interface IMessagingMemberPermissions
{
    /// <returns><c>false</c> when the membership is not of the tenant or is not active.</returns>
    Task<bool> HasAnyAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);
}
