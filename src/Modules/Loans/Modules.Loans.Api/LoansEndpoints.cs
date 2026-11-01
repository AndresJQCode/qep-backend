using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Loans.Application;

namespace Modules.Loans.Api;

public static class LoansEndpoints
{
    public static IEndpointRouteBuilder MapLoansEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como billing y suppliers: la política frena a quien no tiene el permiso y el handler
        // (LoansAuthorization) a quien lo tiene en otro tenant. Las demás rutas del módulo se agregan en las tareas siguientes,
        // sobre este mismo grupo.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/loans")
            .WithTags("Loans");

        // En el orden del contrato: las rutas literales (/assignees, y /installments cuando llegue) van antes de /{loanId:guid}.
        group.MapGet("/", ListLoansAsync)
            .RequireAuthorization(LoansPermissions.Read)
            .Produces<LoanPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", CreateLoanAsync)
            .RequireAuthorization(LoansPermissions.Manage)
            .Accepts<CreateLoanRequest>("application/json")
            .Produces<LoanDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(LoansPermissions.Read)
            .Produces<LoanAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{loanId:guid}", GetLoanAsync)
            .RequireAuthorization(LoansPermissions.Read)
            .Produces<LoanDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{loanId:guid}", UpdateLoanAsync)
            .RequireAuthorization(LoansPermissions.Manage)
            .Accepts<UpdateLoanRequest>("application/json")
            .Produces<LoanDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{loanId:guid}", DeleteLoanAsync)
            .RequireAuthorization(LoansPermissions.Manage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListLoansAsync(
        Guid tenantId,
        string? status,
        string? search,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListLoansQuery.DefaultPageSize)
    {
        var loans = await dispatcher.QueryAsync(
            new ListLoansQuery(
                tenantId,
                string.IsNullOrWhiteSpace(status) ? ListLoansQuery.DefaultStatus : status,
                search,
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(loans);
    }

    private static async Task<IResult> CreateLoanAsync(
        Guid tenantId,
        CreateLoanRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var loan = await dispatcher.SendAsync(CreateLoanCommand.From(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/loans/{loan.Loan.Id}", loan);
    }

    private static async Task<IResult> ListAssigneesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var assignees = await dispatcher.QueryAsync(new ListLoanAssigneesQuery(tenantId), cancellationToken);

        return Results.Ok(assignees);
    }

    private static async Task<IResult> GetLoanAsync(
        Guid tenantId,
        Guid loanId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var loan = await dispatcher.QueryAsync(new GetLoanQuery(tenantId, loanId), cancellationToken);

        return Results.Ok(loan);
    }

    private static async Task<IResult> UpdateLoanAsync(
        Guid tenantId,
        Guid loanId,
        UpdateLoanRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var loan = await dispatcher.SendAsync(
            UpdateLoanCommand.From(tenantId, loanId, request, RequireVersion(httpContext)), cancellationToken);

        return Results.Ok(loan);
    }

    private static async Task<IResult> DeleteLoanAsync(
        Guid tenantId,
        Guid loanId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteLoanCommand(tenantId, loanId), cancellationToken);

        return Results.NoContent();
    }

    // Copia de ServiceContractsEndpoints.RequireVersion (Loans no referencia ese módulo): sin If-Match 428; la versión vieja es 412 en el handler.
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded loan version is required.");

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
