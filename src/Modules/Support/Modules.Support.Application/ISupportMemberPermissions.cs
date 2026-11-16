namespace Modules.Support.Application;

/// <summary>
/// Plan N4: whether a member holds any of <paramref name="permissions"/> right now, read from the database and not from claims. The
/// hub asks long after the handshake (a <c>Subscribe</c> hours later must see today's roles), and it only knows the member id.
/// A separate port and not one more method on <see cref="ISupportMemberDirectory"/>, so no existing double has to change.
/// </summary>
public interface ISupportMemberPermissions
{
    /// <returns><c>false</c> when the membership is not of the tenant or is not active.</returns>
    Task<bool> HasAnyAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);
}
