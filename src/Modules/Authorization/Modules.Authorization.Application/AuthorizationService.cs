using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed class AuthorizationService(
    IMembershipDirectory membershipDirectory,
    ITenantRoleCatalog roleCatalog,
    ITenantModules tenantModules,
    ModuleEntitlementMask entitlementMask,
    IOperatorTenant operatorTenant)
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

        // Paso 2: resolver los permisos acotados al tenant — de sistema y custom. DirectGrant
        // y la Policy contextual quedan diferidos (ver docs/decisions/0002).
        var permissions = await roleCatalog.PermissionsForAsync(tenantId, roles, cancellationToken);

        // Paso 3 (spec 2026-10-07): descartar los de módulos que el tenant no tiene. null no
        // debería pasar —hay membresía activa, luego hay tenant—; si pasa, fail closed: sólo núcleo.
        // AuthorizeAsync hereda el enmascarado.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken) ?? TenantModuleSet.Empty;
        var masked = entitlementMask.Apply(permissions, modules);
        // Paso 4 (spec 2026-10-08 §2): operator.* sólo sobrevive en el tenant operador, aunque un rol
        // personalizado los traiga por SQL.
        return OperatorPermissionFilter.Apply(masked, operatorTenant.IsOperator(tenantId));
    }
}
