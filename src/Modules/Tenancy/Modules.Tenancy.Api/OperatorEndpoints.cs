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

        group.MapPost("/tenants/{targetTenantId:guid}/status", ChangeStatusAsync)
            .RequireAuthorization(OperatorPermissions.TenantsManage)
            .Accepts<ChangeTenantStatusRequest>("application/json")
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/tenants/{targetTenantId:guid}/history", ListHistoryAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorHistoryPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListHistoryAsync(
        Guid tenantId,
        Guid targetTenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? module = null,
        int page = 1,
        int pageSize = 25)
    {
        var result = await dispatcher.QueryAsync(
            new ListTenantHistoryQuery(new TenantId(tenantId), new TenantId(targetTenantId), module, page, pageSize),
            cancellationToken);
        return Results.Ok(result);
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

    private static async Task<IResult> ChangeStatusAsync(
        Guid tenantId,
        Guid targetTenantId,
        ChangeTenantStatusRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // D3: el tenant tiene versión de agregado; sin If-Match es 428 antes de despachar.
        var expectedVersion = IfMatchHeader.RequireVersion(httpContext);
        var result = await dispatcher.SendAsync(
            new ChangeTenantStatusCommand(
                new TenantId(tenantId), new TenantId(targetTenantId), request.Status, request.Reason, request.Note,
                expectedVersion),
            cancellationToken);
        return Results.Ok(result);
    }
}

/// <summary>Spec 2026-10-08 §5: todo nullable, para que un campo ausente, nulo o con un valor desconocido sea
/// 422 del validador y no un 400 del binder. Un tipo JSON equivocado (un número donde va texto, un objeto
/// donde va la lista) sigue siendo 400 del binder, como en el resto de la API. La SPA manda
/// <c>note: null</c> cuando la nota queda en blanco.</summary>
public sealed record ChangeTenantModulesRequest(
    IReadOnlyList<TenantModuleChangeInput>? Changes, string? Reason, string? Note);

/// <summary>Spec 2026-10-08 §5: <c>status</c> es <c>inactive</c> o <c>active</c> (decisión P12 del plan); la
/// versión viaja en el <c>If-Match</c>, no en el cuerpo.</summary>
public sealed record ChangeTenantStatusRequest(string? Status, string? Reason, string? Note);
