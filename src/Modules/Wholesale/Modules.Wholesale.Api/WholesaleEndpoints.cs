using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Api;

/// <summary>
/// Las rutas del mayorista dentro de su tenant (contrato «Listas de precios» y «Distribuidores»). Tenant en la ruta, como
/// suppliers y purchases: la política frena a quien no tiene el permiso (y, con el módulo apagado, <c>ModuleEntitlementMask</c> ya
/// se lo quitó) y el handler (<c>WholesaleAuthorization</c>) a quien lo tiene en otro tenant. Las rutas de la invitación viven en
/// <c>src/Api/WholesaleInvitationEndpoints.cs</c>: componen Identity y Tenancy, que este módulo no ve.
/// </summary>
public static class WholesaleEndpoints
{
    private const string PriceListResource = "price list";

    private const string DistributorResource = "distributor";

    public static IEndpointRouteBuilder MapWholesaleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapPriceLists(endpoints);
        MapDistributors(endpoints);

        // Ruling W5: las categorías para elegir la visibilidad, sin exigir catalog.product.read a quien administra listas.
        endpoints.MapGet("/api/v1/tenants/{tenantId:guid}/wholesale/categories", ListCategoriesAsync)
            .WithTags("Wholesale")
            .RequireAuthorization(WholesalePermissions.PriceListManage)
            .Produces<WholesaleCategoriesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static void MapPriceLists(IEndpointRouteBuilder endpoints)
    {
        // No hay permiso de lectura aparte: todas piden wholesale.price_list.manage.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/wholesale/price-lists")
            .WithTags("Wholesale")
            .RequireAuthorization(WholesalePermissions.PriceListManage);

        group.MapGet("/", ListPriceListsAsync)
            .Produces<PriceListsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreatePriceListAsync)
            .Accepts<PriceListWriteRequest>("application/json")
            .Produces<PriceListDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Ruta literal antes de /{priceListId:guid}. Es un POST que no escribe: la lista sin guardar (D-11).
        group.MapPost("/preview", PreviewPriceListAsync)
            .Accepts<PriceListPreviewRequest>("application/json")
            .Produces<PriceListPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{priceListId:guid}", GetPriceListAsync)
            .Produces<PriceListDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{priceListId:guid}", UpdatePriceListAsync)
            .Accepts<PriceListWriteRequest>("application/json")
            .Produces<PriceListDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{priceListId:guid}", DeletePriceListAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static void MapDistributors(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/wholesale/distributors")
            .WithTags("Wholesale");

        group.MapGet("/", ListDistributorsAsync)
            .RequireAuthorization(WholesalePermissions.DistributorRead)
            .Produces<DistributorsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", InviteDistributorAsync)
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Accepts<DistributorInviteRequest>("application/json")
            .Produces<DistributorDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Ruta literal antes de /{distributorId:guid}: lo que el formulario de invitar o editar elige.
        group.MapGet("/options", GetDistributorOptionsAsync)
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Produces<DistributorOptionsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{distributorId:guid}", GetDistributorAsync)
            .RequireAuthorization(WholesalePermissions.DistributorRead)
            .Produces<DistributorDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{distributorId:guid}", UpdateDistributorAsync)
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Accepts<DistributorUpdateRequest>("application/json")
            .Produces<DistributorDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{distributorId:guid}", DeleteDistributorAsync)
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        MapTransition(group, "/{distributorId:guid}/resend-invitation", DistributorAction.Resend);
        MapTransition(group, "/{distributorId:guid}/block", DistributorAction.Block);
        MapTransition(group, "/{distributorId:guid}/unblock", DistributorAction.Unblock);

        // Spec 2026-11-19 §1, «Copiar»: rota el link y lo devuelve una sola vez en InvitationUrl. Sin cuerpo, con If-Match.
        group.MapPost("/{distributorId:guid}/invitation-link", RotateInvitationLinkAsync)
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Produces<DistributorDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    // Reenviar, bloquear y desbloquear no llevan cuerpo y comparten handler; sólo cambia la acción.
    private static void MapTransition(RouteGroupBuilder group, string pattern, DistributorAction action) =>
        group.MapPost(
                pattern,
                (Guid tenantId, Guid distributorId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    TransitionDistributorAsync(tenantId, distributorId, action, httpContext, dispatcher, cancellationToken))
            .RequireAuthorization(WholesalePermissions.DistributorManage)
            .Produces<DistributorDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static async Task<IResult> ListCategoriesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListWholesaleCategoriesQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListPriceListsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPriceListsQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetPriceListAsync(
        Guid tenantId, Guid priceListId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPriceListQuery(tenantId, priceListId), cancellationToken));

    private static async Task<IResult> CreatePriceListAsync(
        Guid tenantId,
        PriceListWriteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var list = await dispatcher.SendAsync(
            new CreatePriceListCommand(
                tenantId, request.Name, request.DiscountPercent, request.Visibility, request.CategoryIds ?? []),
            cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/wholesale/price-lists/{list.Id}", list);
    }

    private static async Task<IResult> PreviewPriceListAsync(
        Guid tenantId,
        PriceListPreviewRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PreviewPriceListCommand(tenantId, request.DiscountPercent, request.Visibility, request.CategoryIds ?? []),
            cancellationToken));

    private static async Task<IResult> UpdatePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        PriceListWriteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, PriceListResource);
        var list = await dispatcher.SendAsync(
            new UpdatePriceListCommand(
                tenantId,
                priceListId,
                version,
                request.Name,
                request.DiscountPercent,
                request.Visibility,
                request.CategoryIds ?? []),
            cancellationToken);

        return Results.Ok(list);
    }

    private static async Task<IResult> DeletePriceListAsync(
        Guid tenantId,
        Guid priceListId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, PriceListResource);
        await dispatcher.SendAsync(new DeletePriceListCommand(tenantId, priceListId, version), cancellationToken);

        return Results.NoContent();
    }

    // status llega como texto: un valor fuera de los tres estados es un 422 con errors.Status, no un 400 del binding (D-22).
    private static async Task<IResult> ListDistributorsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? status = null,
        string? search = null,
        int page = 1,
        int pageSize = DistributorPaging.DefaultPageSize) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListDistributorsQuery(tenantId, status, search, page, pageSize), cancellationToken));

    private static async Task<IResult> GetDistributorOptionsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDistributorOptionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetDistributorAsync(
        Guid tenantId, Guid distributorId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDistributorQuery(tenantId, distributorId), cancellationToken));

    private static async Task<IResult> InviteDistributorAsync(
        Guid tenantId,
        DistributorInviteRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var distributor = await dispatcher.SendAsync(
            new InviteDistributorCommand(
                tenantId,
                request.Email,
                request.BusinessName,
                request.Country,
                request.IdentificationType,
                request.IdentificationNumber,
                request.ContactName,
                request.PriceListId,
                request.AdvisorId,
                request.BillingCompanyId,
                httpContext.TraceIdentifier,
                request.Phone,
                request.WhatsAppConnectionId),
            cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/wholesale/distributors/{distributor.Id}", distributor);
    }

    private static async Task<IResult> UpdateDistributorAsync(
        Guid tenantId,
        Guid distributorId,
        DistributorUpdateRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, DistributorResource);
        var distributor = await dispatcher.SendAsync(
            new UpdateDistributorCommand(
                tenantId,
                distributorId,
                version,
                request.PriceListId,
                request.AdvisorId,
                request.BillingCompanyId,
                request.Phone,
                request.WhatsAppConnectionId),
            cancellationToken);

        return Results.Ok(distributor);
    }

    private static async Task<IResult> DeleteDistributorAsync(
        Guid tenantId,
        Guid distributorId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, DistributorResource);
        await dispatcher.SendAsync(new DeleteDistributorCommand(tenantId, distributorId, version), cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> TransitionDistributorAsync(
        Guid tenantId,
        Guid distributorId,
        DistributorAction action,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, DistributorResource);
        var distributor = await dispatcher.SendAsync(
            new TransitionDistributorCommand(tenantId, distributorId, version, action, httpContext.TraceIdentifier),
            cancellationToken);

        return Results.Ok(distributor);
    }

    private static async Task<IResult> RotateInvitationLinkAsync(
        Guid tenantId,
        Guid distributorId,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var version = WholesaleIfMatch.RequireVersion(httpContext, DistributorResource);
        var distributor = await dispatcher.SendAsync(
            new RotateInvitationLinkCommand(tenantId, distributorId, version, httpContext.TraceIdentifier),
            cancellationToken);

        return Results.Ok(distributor);
    }
}

/// <summary>El cuerpo de alta y edición de una lista. <c>Visibility</c> como texto: un valor raro es 422, no 400.</summary>
public sealed record PriceListWriteRequest(
    string Name, decimal DiscountPercent, string Visibility, IReadOnlyList<Guid>? CategoryIds);

/// <summary>La lista sin guardar para «Así lo ve el distribuidor»: los términos sin el nombre.</summary>
public sealed record PriceListPreviewRequest(decimal DiscountPercent, string Visibility, IReadOnlyList<Guid>? CategoryIds);

/// <summary>Invitar a un distribuidor: sus datos y lo que el mayorista elige para la relación. Con <c>Phone</c> sale también
/// el aviso por WhatsApp (spec 2026-11-18) por <c>WhatsAppConnectionId</c>, o por la única conexión lista si no viene.</summary>
public sealed record DistributorInviteRequest(
    string Email,
    string BusinessName,
    string Country,
    string IdentificationType,
    string IdentificationNumber,
    string ContactName,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string? Phone = null,
    Guid? WhatsAppConnectionId = null);

/// <summary>Lo que se edita de una relación en cualquier estado; los datos del distribuidor no cambian. <c>Phone</c> y
/// <c>WhatsAppConnectionId</c> sólo cuentan mientras está invitada, y <c>Phone</c> ausente o <c>null</c> los borra.</summary>
public sealed record DistributorUpdateRequest(
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string? Phone = null,
    Guid? WhatsAppConnectionId = null);
