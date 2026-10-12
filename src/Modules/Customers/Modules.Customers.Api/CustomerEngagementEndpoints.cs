using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Customers.Application;

namespace Modules.Customers.Api;

// Spec 2026-10-11 §4.2: las notas, las etiquetas y el consentimiento de marketing. Archivo propio y no más bloques de
// CustomerEndpoints: son recursos distintos que comparten el prefijo de ruta, mismo criterio que
// ClientClassificationEndpoints. Los DTO de Application viajan tal cual: son la forma que la pantalla dibuja.
public static class CustomerEngagementEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEngagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/customers")
            .WithTags("Customers");

        // Notas internas. El borrado tiene su propia política porque lo puede hacer el autor (manage) o un moderador.
        group.MapGet("/{customerId:guid}/notes", ListNotesAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<IReadOnlyList<CustomerNoteDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{customerId:guid}/notes", CreateNoteAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<CreateCustomerNoteRequest>("application/json")
            .Produces<CustomerNoteDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{customerId:guid}/notes/{noteId:guid}", UpdateNoteAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<UpdateCustomerNoteRequest>("application/json")
            .Produces<CustomerNoteDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{customerId:guid}/notes/{noteId:guid}", DeleteNoteAsync)
            .RequireAuthorization(CustomersPolicies.NoteDelete)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Catálogo de etiquetas. `/tags` no choca con `/{customerId:guid}`: la restricción guid lo descarta.
        group.MapGet("/tags", ListTagsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<IReadOnlyList<TagDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/tags", CreateTagAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<CreateTagRequest>("application/json")
            .Produces<TagDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/tags/{tagId:guid}", UpdateTagAsync)
            .RequireAuthorization(CustomersPermissions.TagManage)
            .Accepts<UpdateTagRequest>("application/json")
            .Produces<TagDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/tags/{tagId:guid}", DeleteTagAsync)
            .RequireAuthorization(CustomersPermissions.TagManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Las etiquetas de un cliente: el PUT reemplaza el conjunto entero y devuelve la colección entera y en orden.
        group.MapGet("/{customerId:guid}/tags", ListCustomerTagsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<IReadOnlyList<TagDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{customerId:guid}/tags", SetCustomerTagsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<SetCustomerTagsRequest>("application/json")
            .Produces<IReadOnlyList<TagDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{customerId:guid}/marketing-consent", SetMarketingConsentAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<SetMarketingConsentRequest>("application/json")
            .Produces<MarketingConsentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListNotesAsync(
        Guid tenantId, Guid customerId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListCustomerNotesQuery(tenantId, customerId), cancellationToken));

    private static async Task<IResult> CreateNoteAsync(
        Guid tenantId,
        Guid customerId,
        CreateCustomerNoteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var note = await dispatcher.SendAsync(
            new CreateCustomerNoteCommand(tenantId, customerId, request.Body), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/customers/{customerId}/notes/{note.Id}", note);
    }

    private static async Task<IResult> UpdateNoteAsync(
        Guid tenantId,
        Guid customerId,
        Guid noteId,
        UpdateCustomerNoteRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateCustomerNoteCommand(tenantId, customerId, noteId, request.Body, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> DeleteNoteAsync(
        Guid tenantId, Guid customerId, Guid noteId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteCustomerNoteCommand(tenantId, customerId, noteId), cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ListTagsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTagsQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateTagAsync(
        Guid tenantId, CreateTagRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var tag = await dispatcher.SendAsync(new CreateTagCommand(tenantId, request.Name, request.Color), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/customers/tags/{tag.Id}", tag);
    }

    private static async Task<IResult> UpdateTagAsync(
        Guid tenantId,
        Guid tagId,
        UpdateTagRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateTagCommand(tenantId, tagId, request.Name, request.Color, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> DeleteTagAsync(
        Guid tenantId, Guid tagId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteTagCommand(tenantId, tagId), cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ListCustomerTagsAsync(
        Guid tenantId, Guid customerId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListCustomerTagsQuery(tenantId, customerId), cancellationToken));

    private static async Task<IResult> SetCustomerTagsAsync(
        Guid tenantId,
        Guid customerId,
        SetCustomerTagsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetCustomerTagsCommand(tenantId, customerId, request.TagIds ?? []), cancellationToken));

    private static async Task<IResult> SetMarketingConsentAsync(
        Guid tenantId,
        Guid customerId,
        SetMarketingConsentRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetMarketingConsentCommand(tenantId, customerId, request.OptedOut), cancellationToken));
}
