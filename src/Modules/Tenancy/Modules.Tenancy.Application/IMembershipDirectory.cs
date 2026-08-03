namespace Modules.Tenancy.Application;

/// <summary>
/// Read-only lookup used on each request to validate that a user has an active
/// membership in the requested tenant and to obtain its role references. A tenant id
/// carried by a token or header is only a signal; access is validated here against a
/// live membership (per the tenancy deep-dives).
/// </summary>
public interface IMembershipDirectory
{
    /// <returns>The active membership's role references, or <c>null</c> if the user
    /// has no active membership in the tenant.</returns>
    Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken);
}
