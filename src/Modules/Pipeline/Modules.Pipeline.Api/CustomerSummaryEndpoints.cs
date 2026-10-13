using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 D36 y §6.4: la segunda llamada de la ficha del cliente. Lo sirve Pipeline porque Customers no puede
/// referenciarlo; con 403 la ficha oculta la sección.</summary>
internal static class CustomerSummaryEndpoints
{
    public static void MapCustomerSummaryEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/customers/{customerId:guid}/summary", GetSummaryAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<CustomerDealsSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid tenantId, Guid customerId, Guid? conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCustomerDealsSummaryQuery(tenantId, customerId, conversationId), cancellationToken));
}
