using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pos.Application;

namespace Modules.Pos.Api;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como companies y catalog. Cada endpoint declara su propio
        // RequireAuthorization (spec, «API»): el grupo no lleva política. Los handlers revalidan
        // tenant y permiso (doble capa) y devuelven 403, nunca 404, ante otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/pos")
            .WithTags("Pos");

        group.MapGet("/register", GetRegisterAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Produces<RegisterContextResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/sessions", OpenSessionAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Accepts<OpenCashSessionRequest>("application/json")
            .Produces<PosOpenSessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sessions/{sessionId:guid}/close", CloseSessionAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Accepts<CloseCashSessionRequest>("application/json")
            .Produces<PosSessionSummaryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/sessions", ListSessionsAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosPage<PosSessionSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/sessions/{sessionId:guid}", GetSessionAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosSessionSummaryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/products", SearchProductsAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Produces<PosPage<PosProductResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/products/by-code", FindProductByCodeAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Produces<PosProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales/preview", PreviewSaleAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Accepts<PreviewPosSaleRequest>("application/json")
            .Produces<PosPreviewResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales", CreateSaleAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Accepts<CreatePosSaleRequest>("application/json")
            .Produces<PosSaleResponse>(StatusCodes.Status201Created)
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/sales/{saleId:guid}", GetSaleAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/sales", ListSalesAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosPage<PosSaleListItemResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales/{saleId:guid}/void", VoidSaleAsync)
            .RequireAuthorization(PosPermissions.SaleVoid)
            .Accepts<VoidPosSaleRequest>("application/json")
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetRegisterAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetRegisterContextQuery(tenantId), cancellationToken));

    private static async Task<IResult> OpenSessionAsync(
        Guid tenantId, OpenCashSessionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var session = await dispatcher.SendAsync(
            new OpenCashSessionCommand(tenantId, request.CompanyId, request.OpeningFloat), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/pos/sessions/{session.Id}", session);
    }

    private static async Task<IResult> CloseSessionAsync(
        Guid tenantId,
        Guid sessionId,
        CloseCashSessionRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // El cajero cierra contra el arqueo que vio: la version de GET /pos/register (spec,
        // decisión 31). Mismo contrato que /orders-export-layout: sin If-Match 428, vieja 412.
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded cash session version is required.");
        }

        return Results.Ok(await dispatcher.SendAsync(
            new CloseCashSessionCommand(tenantId, sessionId, expectedVersion, request.CountedCash, request.Note),
            cancellationToken));
    }

    private static async Task<IResult> ListSessionsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        string? status = null,
        int page = 1,
        int pageSize = 20) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListCashSessionsQuery(tenantId, from, to, status, page, pageSize), cancellationToken));

    private static async Task<IResult> GetSessionAsync(
        Guid tenantId, Guid sessionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCashSessionQuery(tenantId, sessionId), cancellationToken));

    private static async Task<IResult> SearchProductsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 40) =>
        Results.Ok(await dispatcher.QueryAsync(
            new SearchPosProductsQuery(tenantId, search, page, pageSize), cancellationToken));

    private static async Task<IResult> FindProductByCodeAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken, string? code = null) =>
        // Sin code, el validador responde 422 con el campo en vez de un 400 del binder.
        Results.Ok(await dispatcher.QueryAsync(
            new FindPosProductByCodeQuery(tenantId, code?.Trim() ?? string.Empty), cancellationToken));

    private static async Task<IResult> PreviewSaleAsync(
        Guid tenantId, PreviewPosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PreviewPosSaleCommand(tenantId, request.Lines ?? []), cancellationToken));

    private static async Task<IResult> CreateSaleAsync(
        Guid tenantId, CreatePosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var creation = await dispatcher.SendAsync(
            new CreatePosSaleCommand(tenantId, request.Id, request.CashSessionId, request.Lines ?? [], request.Payments ?? []),
            cancellationToken);

        // 201 la primera vez; 200 en la repetición reconocida, con el mismo cuerpo (spec, «Crear venta»).
        return creation.Created
            ? Results.Created($"/api/v1/tenants/{tenantId}/pos/sales/{creation.Sale.Id}", creation.Sale)
            : Results.Ok(creation.Sale);
    }

    private static async Task<IResult> GetSaleAsync(
        Guid tenantId, Guid saleId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPosSaleQuery(tenantId, saleId), cancellationToken));

    private static async Task<IResult> ListSalesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? sessionId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? status = null,
        string? number = null,
        int page = 1,
        int pageSize = 20) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListPosSalesQuery(tenantId, sessionId, from, to, status, number, page, pageSize), cancellationToken));

    private static async Task<IResult> VoidSaleAsync(
        Guid tenantId, Guid saleId, VoidPosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new VoidPosSaleCommand(tenantId, saleId, request.Reason), cancellationToken));

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

public sealed record OpenCashSessionRequest(Guid? CompanyId, decimal? OpeningFloat);

public sealed record CloseCashSessionRequest(decimal? CountedCash, string? Note);

public sealed record PreviewPosSaleRequest(IReadOnlyList<PosPreviewLineRequest>? Lines);

public sealed record CreatePosSaleRequest(
    Guid Id,
    Guid CashSessionId,
    IReadOnlyList<PosSaleLineRequest>? Lines,
    IReadOnlyList<PosPaymentRequest>? Payments);

public sealed record VoidPosSaleRequest(string? Reason);
