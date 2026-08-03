using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
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

        return endpoints;
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
