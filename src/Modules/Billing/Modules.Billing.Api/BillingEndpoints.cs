using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Billing.Application;

namespace Modules.Billing.Api;

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como suppliers y customers: la política frena a quien no tiene el
        // permiso y el handler (BillingAuthorization) a quien lo tiene en otro tenant. Las demás
        // rutas del módulo se agregan en las tareas siguientes, sobre este mismo grupo.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/billing")
            .WithTags("Billing");

        group.MapGet("/customers/{customerId:guid}/profile", GetCustomerProfileAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<ProfileDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Upsert sin If-Match (spec §4): la última escritura gana, como los ajustes de cotización.
        group.MapPut("/customers/{customerId:guid}/profile", UpsertCustomerProfileAsync)
            .RequireAuthorization(BillingPermissions.Manage)
            .Accepts<ProfileInput>("application/json")
            .Produces<ProfileDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(BillingPermissions.Read)
            .Produces<AssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetCustomerProfileAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var profile = await dispatcher.QueryAsync(
            new GetCustomerProfileQuery(tenantId, customerId), cancellationToken);

        return Results.Ok(profile);
    }

    private static async Task<IResult> UpsertCustomerProfileAsync(
        Guid tenantId,
        Guid customerId,
        ProfileInput request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var profile = await dispatcher.SendAsync(
            UpsertCustomerProfileCommand.From(tenantId, customerId, request), cancellationToken);

        return Results.Ok(profile);
    }

    private static async Task<IResult> ListAssigneesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var assignees = await dispatcher.QueryAsync(new ListAssigneesQuery(tenantId), cancellationToken);

        return Results.Ok(assignees);
    }
}
