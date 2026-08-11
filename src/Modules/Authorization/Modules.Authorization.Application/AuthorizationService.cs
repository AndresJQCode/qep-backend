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
        // Paso 1: validar la membresía activa (deny por defecto cuando no hay).
        var roles = await membershipDirectory.FindActiveRolesAsync(
            subjectId,
            tenantId,
            cancellationToken);
        if (roles is null)
        {
            return null;
        }

        // Paso 2: resolver los permisos de rol acotados al tenant. DirectGrant y la Policy
        // contextual quedan diferidos (ver docs/decisions/0002).
        return roles
            .SelectMany(roleCatalog.PermissionsFor)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
