using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

/// <summary>
/// Spec 2026-10-08 §5 (D6): la ruta lleva el tenant desde el que actúa el operador (QCode), igual que
/// el resto de la API; el administrado va como <c>{targetTenantId}</c>. Doble capa: la política exige el
/// permiso <c>operator.*</c> y el handler revalida con <c>OperatorAuthorization</c>.
/// </summary>
public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator")
            .WithTags("Operator");

        group.MapGet("/tenants", ListTenantsAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorTenantPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tenants/{targetTenantId:guid}", GetTenantAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tenants/{targetTenantId:guid}/modules/changes", ChangeModulesAsync)
            .RequireAuthorization(OperatorPermissions.ModulesManage)
            .Accepts<ChangeTenantModulesRequest>("application/json")
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListTenantsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 25)
    {
        var result = await dispatcher.QueryAsync(
            new ListOperatorTenantsQuery(new TenantId(tenantId), search, page, pageSize), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetTenantAsync(
        Guid tenantId,
        Guid targetTenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetOperatorTenantQuery(new TenantId(tenantId), new TenantId(targetTenantId)), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ChangeModulesAsync(
        Guid tenantId,
        Guid targetTenantId,
        ChangeTenantModulesRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new ChangeTenantModulesCommand(
                new TenantId(tenantId), new TenantId(targetTenantId), request.Changes, request.Reason, request.Note),
            cancellationToken);
        return Results.Ok(result);
    }
}

/// <summary>Spec 2026-10-08 §5: todo nullable, para que una forma mala sea 422 del validador y no un 400
/// del binder. La SPA manda <c>note: null</c> cuando la nota queda en blanco.</summary>
public sealed record ChangeTenantModulesRequest(
    IReadOnlyList<TenantModuleChangeInput>? Changes, string? Reason, string? Note);
