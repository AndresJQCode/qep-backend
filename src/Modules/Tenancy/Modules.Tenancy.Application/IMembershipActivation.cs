namespace Modules.Tenancy.Application;

/// <summary>
/// Published cross-module contract that accepts a user's pending tenant invitations
/// on login. Per ADR 0016 the first successful external login transitions the user's
/// <c>Invited</c> memberships to <c>Active</c>. Expired invitations are skipped (and
/// marked expired). Consumed by the composition-root <c>/auth/session</c> endpoint.
/// </summary>
public interface IMembershipActivation
{
    /// <returns>The tenant ids the user is active in after acceptance.</returns>
    Task<IReadOnlyCollection<Guid>> AcceptInvitedMembershipsAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}
