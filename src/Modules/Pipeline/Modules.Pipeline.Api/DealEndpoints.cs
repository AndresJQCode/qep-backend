using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>Spec 2026-10-13 §6: los negocios. Toda escritura recibe <c>version</c> y devuelve el negocio completo.</summary>
internal static class DealEndpoints
{
    public static void MapDealEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var deals = pipeline.MapGroup("/deals");

        deals.MapPost("/", CreateAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<CreateDealRequest>("application/json")
            .Produces<DealDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        deals.MapGet("/{dealId:guid}", GetAsync)
            .RequireAuthorization(PipelinePermissions.DealRead)
            .Produces<DealDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        deals.MapPatch("/{dealId:guid}", UpdateAsync).WithWriteContract<UpdateDealRequest>();
        deals.MapPost("/{dealId:guid}/move", MoveAsync).WithWriteContract<MoveDealRequest>();
        deals.MapPost("/{dealId:guid}/win", WinAsync).WithWriteContract<DealVersionRequest>();
        deals.MapPost("/{dealId:guid}/lose", LoseAsync).WithWriteContract<LoseDealRequest>();
        deals.MapPost("/{dealId:guid}/reopen", ReopenAsync).WithWriteContract<DealVersionRequest>();
    }

    /// <summary>Lo común de toda escritura sobre un negocio existente.</summary>
    internal static RouteHandlerBuilder WithWriteContract<TRequest>(this RouteHandlerBuilder route)
        where TRequest : notnull =>
        route.RequireAuthorization(PipelinePermissions.DealManage)
            .Accepts<TRequest>("application/json")
            .Produces<DealDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    // Sin Location: la pantalla repinta con el negocio que vuelve.
    private static async Task<IResult> CreateAsync(Guid tenantId, CreateDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateDealCommand(
                tenantId, request.CustomerId, request.Title, request.Amount, request.Currency, request.ExpectedCloseDate, request.OwnerMemberId), cancellationToken));

    private static async Task<IResult> GetAsync(Guid tenantId, Guid dealId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDealQuery(tenantId, dealId), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid dealId, UpdateDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new UpdateDealCommand(
            tenantId, dealId, request.Title, request.OwnerMemberId, request.Amount, request.Currency, request.ExpectedCloseDate, request.Version), cancellationToken));

    private static async Task<IResult> MoveAsync(
        Guid tenantId, Guid dealId, MoveDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new MoveDealCommand(tenantId, dealId, request.StageId, request.Version), cancellationToken));

    private static async Task<IResult> WinAsync(
        Guid tenantId, Guid dealId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new WinDealCommand(tenantId, dealId, request.Version), cancellationToken));

    private static async Task<IResult> LoseAsync(
        Guid tenantId, Guid dealId, LoseDealRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new LoseDealCommand(tenantId, dealId, request.LossReasonId, request.Note, request.Version), cancellationToken));

    private static async Task<IResult> ReopenAsync(
        Guid tenantId, Guid dealId, DealVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReopenDealCommand(tenantId, dealId, request.Version), cancellationToken));
}

/// <summary>Todo opcional: lo que falte sale 422 con su clave en <c>errors</c>, no 400 del binder.</summary>
public sealed record CreateDealRequest(Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId);

/// <summary>P5 del plan: todos los editables, que reemplazan a los actuales.</summary>
public sealed record UpdateDealRequest(string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version);

public sealed record MoveDealRequest(Guid? StageId, long? Version);

public sealed record DealVersionRequest(long? Version);

public sealed record LoseDealRequest(Guid? LossReasonId, string? Note, long? Version);
