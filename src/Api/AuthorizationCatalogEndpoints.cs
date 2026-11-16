using System.Security.Claims;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

public static class AuthorizationCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationCatalogEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/api/v1/tenants/{tenantId:guid}/authorization/catalog", GetCatalogAsync)
            .WithTags("Authorization")
            .RequireAuthorization(TenancyPermissions.AdvisorshipRead)
            .Produces<AuthorizationCatalogResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Sólo autenticación, deliberadamente sin requerir permiso: exigir uno para saber qué
        // permisos tenés es circular, y volvería la respuesta inalcanzable justo para los sujetos
        // cuya respuesta es "casi nada" — el caso que un cliente más necesita para renderizar
        // bien. No lee más que los claims que el request ya trae, así que no expone ningún dato
        // que el llamador no haya traído.
        // Lo agregó AUTH-04 (SDD-OD-10): nada más exponía los permisos efectivos.
        endpoints
            .MapGet("/api/v1/tenants/{tenantId:guid}/authorization/me", GetEffectivePermissions)
            .WithTags("Authorization")
            .RequireAuthorization()
            .Produces<EffectivePermissionsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static IResult GetEffectivePermissions(Guid tenantId, HttpContext httpContext)
    {
        var user = httpContext.User;

        // Se leen los claims directo y no por IExecutionContext: su TenantId tira excepción
        // cuando el claim no está, y que no esté es un resultado normal acá — un llamador
        // sin membresía activa en el tenant pedido nunca consigue que se le resuelva el claim
        // (ExternalClaimsTransformation.cs:112-118). Eso merece un 403, no un 500.
        var claimedTenant = user.FindFirstValue(QepClaimTypes.TenantId);
        if (!Guid.TryParse(claimedTenant, out var authenticatedTenant) ||
            authenticatedTenant != tenantId)
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read effective permissions for this tenant.");
        }

        var userId = user.FindFirstValue(QepClaimTypes.QepSubject)
            ?? user.FindFirstValue(QepClaimTypes.SubjectId);
        if (!Guid.TryParse(userId, out var subjectId))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject is not linked to a QEP user.");
        }

        // Orden ordinal para que la respuesta sea estable entre requests: un orden inestable
        // haría girar los cachés del cliente y volvería el payload inútil como señal de cambio.
        var permissions = user.Claims
            .Where(claim => claim.Type == QepClaimTypes.Permission)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new EffectivePermissionsResponse(tenantId, subjectId, permissions));
    }

    // Spec 2026-10-07, «Catálogo de roles filtrado»: con el tenant en tenancy.tenants, saca de
    // permissions y de cada roles[].permissions lo que el enmascarado descartaría. El enmascarado por
    // módulos no se aplica a /authorization/roles ni a EnsureKnownPermissions: son lo guardado, y si
    // el módulo vuelve el rol vuelve a conceder lo que concedía. La excepción es operator.* (spec
    // 2026-10-08 §2), que /authorization/roles también oculta fuera del tenant operador.
    // CatalogVersion no cambia: describe el catálogo del build.
    private static async Task<IResult> GetCatalogAsync(
        Guid tenantId,
        IExecutionContext executionContext,
        IRoleCatalog roleCatalog,
        ITenantModules tenantModules,
        ModuleEntitlementMask entitlementMask,
        IOperatorTenant operatorTenant,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId != new TenantId(tenantId))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the authorization catalog for this tenant.");
        }

        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        var isOperator = operatorTenant.IsOperator(tenantId);
        bool Offered(string permission) => modules is null || entitlementMask.Allows(permission, modules);
        // Spec 2026-10-08 §2 y D11: operator.* nunca es una casilla (ningún rol personalizado puede
        // llevarlo), y en roles[] sólo lo ve el tenant operador (lo resuelve RoleAssignability).
        bool OfferedAsCheckbox(string permission) =>
            Offered(permission) && !OperatorPermissionFilter.IsOperatorPermission(permission);

        return Results.Ok(new AuthorizationCatalogResponse(
            roleCatalog.CatalogVersion,
            roleCatalog.ListRoles()
                .Select(role =>
                {
                    // Spec 2026-11-15: el mismo cálculo que RoleReferenceValidator, para que la pantalla
                    // no ofrezca un rol que la API rechaza con role_unavailable.
                    var offered = RoleAssignability.OfferedPermissions(
                        role.Permissions, modules, isOperator, entitlementMask);
                    return new RoleCatalogItemResponse(
                        role.Role,
                        role.DisplayName,
                        role.Description,
                        role.Category,
                        role.RiskLevel,
                        offered.OrderBy(permission => permission, StringComparer.Ordinal).ToArray(),
                        offered.Count > 0);
                })
                .ToArray(),
            roleCatalog.ListPermissions()
                .Where(permission => OfferedAsCheckbox(permission.Permission))
                .Select(ToResponse)
                .ToArray(),
            // Spec 2026-11-16: las columnas de la matriz, en orden y con su nombre en pantalla.
            PermissionVocabulary.ColumnActions
                .Select(action => new CatalogActionResponse(action, PermissionVocabulary.LabelOfAction(action)))
                .ToArray()));
    }

    private static PermissionCatalogItemResponse ToResponse(PermissionDefinition permission)
    {
        var placement = PermissionVocabulary.PlacementOf(permission);
        return new PermissionCatalogItemResponse(
            permission.Permission,
            permission.DisplayName,
            permission.Description,
            permission.Category,
            permission.RiskLevel,
            PermissionVocabulary.LabelOfCategory(permission.Category),
            placement.Entity,
            placement.EntityLabel,
            placement.Action,
            placement.ActionLabel);
    }
}

public sealed record AuthorizationCatalogResponse(
    string CatalogVersion,
    IReadOnlyCollection<RoleCatalogItemResponse> Roles,
    IReadOnlyCollection<PermissionCatalogItemResponse> Permissions,
    // Spec 2026-11-16: las columnas de la matriz de roles. Una acción de permiso fuera de esta lista
    // va a «Otros» de su fila.
    IReadOnlyCollection<CatalogActionResponse> Actions);

public sealed record CatalogActionResponse(string Key, string Label);

public sealed record RoleCatalogItemResponse(
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<string> Permissions,
    // Spec 2026-11-15: false si el rol no concede ningún permiso que el tenant ofrece (Cajero sin pos).
    // La pantalla no lo ofrece al invitar y sólo deja quitarlo a quien ya lo tiene; la API rechaza
    // agregarlo con 422 tenancy.membership.role_unavailable.
    bool Assignable);

public sealed record PermissionCatalogItemResponse(
    string Permission,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    // Spec 2026-11-16: fila (Entity) y columna (Action) del permiso en la matriz de roles, con los
    // nombres en pantalla. Sin metadata, Action es "other" y la fila es el propio permiso.
    string CategoryLabel,
    string Entity,
    string EntityLabel,
    string Action,
    string ActionLabel);

/// <summary>
/// Lo que el llamador realmente puede hacer en este tenant, a diferencia de qué roles existen.
/// La SPA lo consume para ocultar acciones que no puede ejecutar — un control de usabilidad.
/// El backend igual autoriza cada request sin importar lo que esto haya devuelto.
/// </summary>
public sealed record EffectivePermissionsResponse(
    Guid TenantId,
    Guid UserId,
    IReadOnlyCollection<string> Permissions);
