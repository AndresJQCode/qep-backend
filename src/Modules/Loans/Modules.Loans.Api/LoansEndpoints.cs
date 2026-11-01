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

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(LoansPermissions.Read)
            .Produces<LoanAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListAssigneesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var assignees = await dispatcher.QueryAsync(new ListLoanAssigneesQuery(tenantId), cancellationToken);

        return Results.Ok(assignees);
    }
}