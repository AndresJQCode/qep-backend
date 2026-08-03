using Modules.Tenancy.Application;

namespace Modules.Authorization.Application;

public sealed class AuthorizationService(
    IMembershipDirectory membershipDirectory,
    IRoleCatalog roleCatalog)
    : IAuthorizationService
{
    public async Task<AuthorizationDecision> AuthorizeAsync(
        Guid subjectId,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken)
    {
        var permissions = await ResolvePermissionsAsync(subjectId, tenantId, cancellationToken);
        if (permissions is null)
        {
            return AuthorizationDecision.Deny("no_active_membership");
        }

        return permissions.Contains(permission, StringComparer.Ordinal)
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny("permission_denied");
    }

    public async Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(
        Guid subjectId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Step 1: validate active membership (deny-by-default when absent).
        var roles = await membershipDirectory.FindActiveRolesAsync(
            subjectId,
            tenantId,
            cancellationToken);
        if (roles is null)
        {
            return null;
        }

        // Step 2: resolve tenant-scoped role permissions. DirectGrant and contextual
        // Policy are deferred (see docs/decisions/0002).
        return roles
            .SelectMany(roleCatalog.PermissionsFor)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
