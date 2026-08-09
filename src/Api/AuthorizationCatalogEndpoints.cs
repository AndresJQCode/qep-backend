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

        // Authentication only, deliberately no permission requirement: demanding one to
        // learn which permissions you hold is circular, and it would make the answer
        // unreachable for exactly the subjects whose answer is "almost nothing" — the case
        // a client most needs in order to render correctly. Reads nothing but the claims
        // the request already carries, so it exposes no data the caller did not bring.
        // Added by AUTH-04 (SDD-OD-10): nothing else exposed effective permissions.
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

        // Read the claims directly rather than through IExecutionContext: its TenantId
        // throws when the claim is absent, and absent is a normal outcome here — a caller
        // with no active membership in the requested tenant never gets the claim resolved
        // (ExternalClaimsTransformation.cs:112-118). That deserves a 403, not a 500.
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

        // Ordinal sort so the response is stable across requests: an unstable order would
        // churn client caches and make the payload useless as a change signal.
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
/// What the caller may actually do in this tenant, as opposed to what roles exist.
/// Consumed by the SPA to hide actions it cannot perform — a usability control. The
/// backend still authorizes every request regardless of what this returned.
/// </summary>
public sealed record EffectivePermissionsResponse(
    Guid TenantId,
    Guid UserId,
    IReadOnlyCollection<string> Permissions);
