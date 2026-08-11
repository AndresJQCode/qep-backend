using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
using Bootstrapper.Authentication;
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
            .RequireAuthorization(TenancyPermissions.MembershipRead)
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

    private static IResult GetCatalogAsync(
        Guid tenantId,
        IExecutionContext executionContext,
        IRoleCatalog roleCatalog)
    {
        if (executionContext.TenantId != new TenantId(tenantId))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the authorization catalog for this tenant.");
        }

        return Results.Ok(new AuthorizationCatalogResponse(
            roleCatalog.CatalogVersion,
            roleCatalog.ListRoles()
                .Select(role => new RoleCatalogItemResponse(
                    role.Role,
                    role.DisplayName,
                    role.Description,
                    role.Category,
                    role.RiskLevel,
                    role.Permissions.OrderBy(permission => permission, StringComparer.Ordinal).ToArray()))
                .ToArray(),
            roleCatalog.ListPermissions()
                .Select(permission => new PermissionCatalogItemResponse(
                    permission.Permission,
                    permission.DisplayName,
                    permission.Description,
                    permission.Category,
                    permission.RiskLevel))
                .ToArray()));
    }
}

public sealed record AuthorizationCatalogResponse(
    string CatalogVersion,
    IReadOnlyCollection<RoleCatalogItemResponse> Roles,
    IReadOnlyCollection<PermissionCatalogItemResponse> Permissions);

public sealed record RoleCatalogItemResponse(
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<string> Permissions);

public sealed record PermissionCatalogItemResponse(
    string Permission,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel);

/// <summary>
/// Lo que el llamador realmente puede hacer en este tenant, a diferencia de qué roles existen.
/// La SPA lo consume para ocultar acciones que no puede ejecutar — un control de usabilidad.
/// El backend igual autoriza cada request sin importar lo que esto haya devuelto.
/// </summary>
public sealed record EffectivePermissionsResponse(
    Guid TenantId,
    Guid UserId,
    IReadOnlyCollection<string> Permissions);
