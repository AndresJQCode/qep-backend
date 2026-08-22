using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Customers.Application;

namespace Modules.Customers.Api;

// Archivo propio y no un bloque mas de CustomerEndpoints: son dos recursos distintos, y un
// archivo que mapea dos deja de decir su nombre. Comparten el prefijo de ruta, no el archivo —
// mismo criterio que TaxRateEndpoints frente a ProductEndpoints en Catalog.
public static class ClientClassificationEndpoints
{
    public static IEndpointRouteBuilder MapClientClassificationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/customers/classifications")
            .WithTags("Customers");

        group.MapGet("/", ListClientClassificationsAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationRead)
            .Produces<ClientClassificationsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{classificationId:guid}", GetClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationRead)
            .Produces<ClientClassificationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationManage)
            .Accepts<CreateClientClassificationRequest>("application/json")
            .Produces<ClientClassificationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{classificationId:guid}", UpdateClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationManage)
            .Accepts<UpdateClientClassificationRequest>("application/json")
            .Produces<ClientClassificationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{classificationId:guid}/activate", ActivateClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationManage)
            .Produces<ClientClassificationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{classificationId:guid}/deactivate", DeactivateClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationManage)
            .Produces<ClientClassificationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{classificationId:guid}", DeleteClientClassificationAsync)
            .RequireAuthorization(CustomersPermissions.ClassificationManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListClientClassificationsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classifications = await dispatcher.QueryAsync(
            new ListClientClassificationsQuery(tenantId),
            cancellationToken);

        return Results.Ok(
            new ClientClassificationsResponse(classifications.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> GetClientClassificationAsync(
        Guid tenantId,
        Guid classificationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classification = await dispatcher.QueryAsync(
            new GetClientClassificationQuery(tenantId, classificationId),
            cancellationToken);

        return Results.Ok(ToResponse(classification));
    }

    private static async Task<IResult> CreateClientClassificationAsync(
        Guid tenantId,
        CreateClientClassificationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classification = await dispatcher.SendAsync(
            new CreateClientClassificationCommand(tenantId, request.Name, request.Prefix),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/customers/classifications/{classification.Id}",
            ToResponse(classification));
    }

    private static async Task<IResult> UpdateClientClassificationAsync(
        Guid tenantId,
        Guid classificationId,
        UpdateClientClassificationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classification = await dispatcher.SendAsync(
            new UpdateClientClassificationCommand(
                tenantId, classificationId, request.Name, request.Prefix),
            cancellationToken);

        return Results.Ok(ToResponse(classification));
    }

    private static async Task<IResult> ActivateClientClassificationAsync(
        Guid tenantId,
        Guid classificationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classification = await dispatcher.SendAsync(
            new ActivateClientClassificationCommand(tenantId, classificationId),
            cancellationToken);

        return Results.Ok(ToResponse(classification));
    }

    private static async Task<IResult> DeactivateClientClassificationAsync(
        Guid tenantId,
        Guid classificationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var classification = await dispatcher.SendAsync(
            new DeactivateClientClassificationCommand(tenantId, classificationId),
            cancellationToken);

        return Results.Ok(ToResponse(classification));
    }

    private static async Task<IResult> DeleteClientClassificationAsync(
        Guid tenantId,
        Guid classificationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteClientClassificationCommand(tenantId, classificationId),
            cancellationToken);

        return Results.NoContent();
    }

    private static ClientClassificationResponse ToResponse(ClientClassificationDto classification) =>
        new(
            classification.Id,
            classification.Name,
            classification.Prefix,
            classification.IsActive,
            classification.CreatedAt,
            classification.UpdatedAt);
}
