using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;

namespace Modules.Quotations.Api;

public static class QuotationEndpoints
{
    // TEMPORAL (a pedido, 2026-08-24): las políticas por permiso (QuotationsPermissions.*)
    // quedan comentadas mientras se prueba el flujo manualmente sin tener que armar
    // X-Permissions en cada request. RequireAuthorization() sin argumentos sigue exigiendo
    // autenticación (el stub de desarrollo la da con sólo X-Subject-Id/X-Tenant-Id); lo que se
    // desactiva es el permiso específico. QuotationsAuthorization.EnsureAuthorized, en el
    // handler, tiene el mismo interruptor. Reactivar los argumentos comentados antes de
    // producción.
    public static IEndpointRouteBuilder MapQuotationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations")
            .WithTags("Quotations");

        group.MapGet("/", ListQuotationsAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationRead */)
            .Produces<QuotationsPageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{quotationId:guid}", GetQuotationAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationRead */)
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // US-17: la linea de tiempo de la cotizacion -- quien, cuando y que cambio. Ruta aparte
        // y no un campo del detalle: crece sin techo con cada edicion, y la pantalla del detalle
        // se pinta sin ella.
        group.MapGet("/{quotationId:guid}/history", ListQuotationHistoryAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationRead */)
            .Produces<QuotationHistoryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateQuotationAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<CreateQuotationRequest>("application/json")
            .Produces<QuotationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{quotationId:guid}", UpdateQuotationAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<UpdateQuotationRequest>("application/json")
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{quotationId:guid}/client", ChangeQuotationClientAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<ChangeQuotationClientRequest>("application/json")
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{quotationId:guid}/items", AddQuotationItemAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<AddQuotationItemRequest>("application/json")
            .Produces<QuotationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{quotationId:guid}/items/{itemId:guid}", UpdateQuotationItemAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<UpdateQuotationItemRequest>("application/json")
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{quotationId:guid}/items/{itemId:guid}", RemoveQuotationItemAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // US-12: el PDF ya se subió a Storage por fuera de este llamado (flujo de carga firmada
        // que Storage ya expone); acá sólo se referencia el archivo y se marca como enviada.
        group.MapPost("/{quotationId:guid}/send", SendQuotationAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Accepts<SendQuotationRequest>("application/json")
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // US-11. Sin cuerpo: no hay motivo obligatorio en las historias de usuario.
        group.MapPost("/{quotationId:guid}/void", VoidQuotationAsync)
            .RequireAuthorization(/* QuotationsPermissions.QuotationManage */)
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListQuotationsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        Guid? advisorId = null,
        string? status = null,
        DateOnly? createdFrom = null,
        DateOnly? createdTo = null,
        string? clientNit = null,
        string? quotationNumber = null,
        int page = 1,
        int pageSize = QuotationPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListQuotationsQuery(
                tenantId, clientId, advisorId, status, createdFrom, createdTo, clientNit,
                quotationNumber, page, pageSize),
            cancellationToken);

        return Results.Ok(new QuotationsPageResponse(
            result.Items.Select(ToListItemResponse).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> GetQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.QueryAsync(
            new GetQuotationQuery(tenantId, quotationId), cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> ListQuotationHistoryAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var entries = await dispatcher.QueryAsync(
            new ListQuotationHistoryQuery(tenantId, quotationId),
            cancellationToken);

        return Results.Ok(new QuotationHistoryResponse(entries));
    }

    private static async Task<IResult> CreateQuotationAsync(
        Guid tenantId,
        CreateQuotationRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new CreateQuotationCommand(
                tenantId,
                request.ClientId,
                request.ValidUntil,
                request.PaymentMethod,
                request.Notes,
                request.Parties,
                request.BillingAccount),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}",
            await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> UpdateQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        UpdateQuotationRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new UpdateQuotationCommand(
                tenantId,
                quotationId,
                request.ValidUntil,
                request.PaymentMethod,
                request.Notes,
                request.Parties,
                request.BillingAccount),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    // US-2 (revisada): cambiar el cliente arrastra las partes y los totales, asi que va por su
    // propia ruta y no como un campo mas del PATCH. Ver ChangeQuotationClientHandler.
    private static async Task<IResult> ChangeQuotationClientAsync(
        Guid tenantId,
        Guid quotationId,
        ChangeQuotationClientRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new ChangeQuotationClientCommand(tenantId, quotationId, request.ClientId),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> AddQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        AddQuotationItemRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new AddQuotationItemCommand(tenantId, quotationId, request.ProductId, request.Quantity),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotationId}",
            await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> UpdateQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        Guid itemId,
        UpdateQuotationItemRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new UpdateQuotationItemCommand(tenantId, quotationId, itemId, request.Quantity),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> RemoveQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        Guid itemId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new RemoveQuotationItemCommand(tenantId, quotationId, itemId),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> SendQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        SendQuotationRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new SendQuotationCommand(tenantId, quotationId, request.PdfFileId),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static async Task<IResult> VoidQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new VoidQuotationCommand(tenantId, quotationId),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }

    private static QuotationListItemResponse ToListItemResponse(QuotationListItemDto quotation) => new(
        quotation.Id,
        quotation.QuotationNumber,
        quotation.ClientId,
        quotation.ClientName,
        quotation.AdvisorId,
        quotation.AdvisorEmail,
        quotation.Status,
        quotation.CreatedAt,
        quotation.Currency,
        quotation.Total);

}
