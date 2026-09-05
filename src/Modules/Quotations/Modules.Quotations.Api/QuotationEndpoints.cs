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
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.QueryAsync(
            new GetQuotationQuery(tenantId, quotationId), cancellationToken);

        return Results.Ok(ToResponse(quotation));
    }

    private static async Task<IResult> CreateQuotationAsync(
        Guid tenantId,
        CreateQuotationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new CreateQuotationCommand(
                tenantId,
                request.ClientId,
                request.ValidUntil,
                request.PaymentMethod,
                request.Notes,
                request.Parties),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}",
            ToResponse(quotation));
    }

    private static async Task<IResult> UpdateQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        UpdateQuotationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new UpdateQuotationCommand(
                tenantId,
                quotationId,
                request.ValidUntil,
                request.PaymentMethod,
                request.Notes,
                request.Parties),
            cancellationToken);

        return Results.Ok(ToResponse(quotation));
    }

    private static async Task<IResult> AddQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        AddQuotationItemRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new AddQuotationItemCommand(tenantId, quotationId, request.ProductId, request.Quantity),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/quotations/{quotationId}",
            ToResponse(quotation));
    }

    private static async Task<IResult> UpdateQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        Guid itemId,
        UpdateQuotationItemRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new UpdateQuotationItemCommand(tenantId, quotationId, itemId, request.Quantity),
            cancellationToken);

        return Results.Ok(ToResponse(quotation));
    }

    private static async Task<IResult> RemoveQuotationItemAsync(
        Guid tenantId,
        Guid quotationId,
        Guid itemId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new RemoveQuotationItemCommand(tenantId, quotationId, itemId),
            cancellationToken);

        return Results.Ok(ToResponse(quotation));
    }

    private static async Task<IResult> SendQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        SendQuotationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new SendQuotationCommand(tenantId, quotationId, request.PdfFileId),
            cancellationToken);

        return Results.Ok(ToResponse(quotation));
    }

    private static async Task<IResult> VoidQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new VoidQuotationCommand(tenantId, quotationId),
            cancellationToken);

        return Results.Ok(ToResponse(quotation));
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
        quotation.Total);

    private static QuotationPartyResponse ToResponse(QuotationPartyDto party) => new(
        party.Id,
        party.Role,
        party.Name,
        party.Phone,
        party.Email,
        party.Address,
        party.DepartmentId,
        party.CityId);

    private static QuotationResponse ToResponse(QuotationDto quotation) => new(
        quotation.Id,
        quotation.QuotationNumber,
        quotation.ClientId,
        quotation.AdvisorId,
        quotation.Status,
        quotation.CreatedAt,
        quotation.ValidUntil,
        quotation.PaymentMethod,
        quotation.Subtotal,
        quotation.TaxPercentage,
        quotation.TaxAmount,
        quotation.DiscountAmount,
        quotation.Total,
        quotation.CustomerVatSurplus,
        quotation.RetentionAmount,
        quotation.NetTotal,
        quotation.Notes,
        quotation.Parties.Select(ToResponse).ToArray(),
        quotation.CreatedBy,
        quotation.UpdatedBy,
        quotation.UpdatedAt,
        quotation.SentAt,
        quotation.PdfFileId,
        quotation.Items
            .Select(item => new QuotationItemResponse(
                item.Id,
                item.ProductId,
                item.Quantity,
                item.UnitPrice,
                item.DiscountPercentage,
                item.DiscountAmount,
                item.Subtotal,
                item.TaxPercentage,
                item.TaxAmount,
                item.Position))
            .ToArray());
}
