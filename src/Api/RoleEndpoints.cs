using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

public static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/authorization/roles")
            .WithTags("Authorization");

        // Leer exige `advisorship.read`, no el permiso de escritura: quien asigna roles a una
        // persona necesita ver qué concede cada uno, y esa capacidad es `advisorship.manage`.
        group.MapGet("/", ListAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipRead)
            .Produces<IReadOnlyList<RoleResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipRolesManage)
            .Accepts<RoleWriteRequest>("application/json")
            .Produces<RoleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Con If-Match, a diferencia de suspender o remover un miembro: acá sí hay una versión
        // que el backend comprueba, porque dos personas editando los permisos de un mismo rol
        // es una carrera real y perderla en silencio significa conceder o quitar accesos que
        // nadie decidió. Mismo contrato que `PATCH /memberships/{id}/roles`.
        group.MapPatch("/{roleId:guid}", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipRolesManage)
            .Accepts<RoleWriteRequest>("application/json")
            .Produces<RoleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{roleId:guid}", DeleteAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipRolesManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var roles = await dispatcher.QueryAsync(
            new ListTenantRolesQuery(new TenantId(tenantId)),
            cancellationToken);

        return Results.Ok(roles.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        RoleWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var role = await dispatcher.SendAsync(
            new CreateRoleCommand(
                new TenantId(tenantId),
                request.Key ?? string.Empty,
                request.DisplayName ?? string.Empty,
                request.Description ?? string.Empty,
                request.Permissions ?? []),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/authorization/roles/{role.Id}",
            ToResponse(role));
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid roleId,
        RoleWriteRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded role version is required.");
        }

        var role = await dispatcher.SendAsync(
            new UpdateRoleCommand(
                new TenantId(tenantId),
                new RoleId(roleId),
                request.DisplayName ?? string.Empty,
                request.Description ?? string.Empty,
                request.Permissions ?? [],
                expectedVersion),
            cancellationToken);

        httpContext.Response.Headers.ETag = $"\"{role.Version}\"";
        return Results.Ok(ToResponse(role));
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        Guid roleId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteRoleCommand(new TenantId(tenantId), new RoleId(roleId)),
            cancellationToken);

        return Results.NoContent();
    }

    private static RoleResponse ToResponse(Role role) =>
        new(
            role.Id.Value,
            role.Key,
            role.DisplayName,
            role.Description,
            "Custom",
            // Un rol recién escrito se responde sin nivel de riesgo calculado: el que ve la
            // lista lo deriva `TenantRoleCatalog` de los permisos, y duplicar ese cálculo acá
            // daría dos fuentes para el mismo dato.
            null,
            role.Permissions,
            IsSystem: false,
            role.Version);

    private static RoleResponse ToResponse(TenantRoleDefinition role) =>
        new(
            // Un rol de sistema no tiene fila ni id: se identifica por su clave, que es lo que
            // viaja en la membresía. Inventarle un Guid haría creer que se puede pedir por id.
            null,
            role.Role,
            role.DisplayName,
            role.Description,
            role.Category,
            role.RiskLevel,
            role.Permissions,
            role.IsSystem,
            role.Version);

    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

public sealed record RoleWriteRequest(
    string? Key,
    string? DisplayName,
    string? Description,
    IReadOnlyCollection<string>? Permissions);

public sealed record RoleResponse(
    Guid? Id,
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string? RiskLevel,
    IReadOnlyCollection<string> Permissions,
    bool IsSystem,
    long Version);
