namespace Modules.Identity.Application;

/// <summary>
/// Published cross-module contract of the Identity module. Other modules depend on
/// this interface (not on Identity internals) to obtain an internal user id for an
/// invited email. It returns a plain <see cref="Guid"/> so callers reference users by
/// id only, per ADR 0016.
/// </summary>
public interface IIdentityProvisioning
{
    /// <summary>
    /// Returns the id of the user for <paramref name="email"/>, creating an invited
    /// user if none exists. Idempotent: repeated calls for the same email return the
    /// same user id and never create duplicates.
    /// </summary>
    Task<Guid> GetOrProvisionInvitedUserAsync(string email, CancellationToken cancellationToken);
}
