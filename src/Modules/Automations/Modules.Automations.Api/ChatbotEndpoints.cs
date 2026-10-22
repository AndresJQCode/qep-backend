using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4: lista, creación, lectura, edición, borrador y validación del chatbot. Leer y validar con
/// <c>automations.chatbot.read</c>; escribir con <c>automations.chatbot.manage</c>, con <c>If-Match</c> sobre un chatbot existente.</summary>
internal static class ChatbotEndpoints
{
    public static void MapChatbotEndpoints(this RouteGroupBuilder automations)
    {
        ArgumentNullException.ThrowIfNull(automations);
        var chatbots = automations.MapGroup("/chatbots");

        chatbots.MapGet("/", ListAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<ChatbotListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        chatbots.MapPost("/", CreateAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<CreateChatbotRequest>("application/json")
            .Produces<ChatbotEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        chatbots.MapGet("/{chatbotId:guid}", GetAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<ChatbotEditorDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        chatbots.MapPut("/{chatbotId:guid}", UpdateAsync)
            .WithWriteContract<UpdateChatbotRequest, ChatbotEditorDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        chatbots.MapPut("/{chatbotId:guid}/draft", SaveDraftAsync)
            .WithWriteContract<SaveDraftRequest, DraftSavedDto>();

        chatbots.MapPost("/{chatbotId:guid}/validate", ValidateAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotRead)
            .Produces<IReadOnlyList<FlowIssueDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Lo común de toda escritura sobre un chatbot existente: <c>If-Match</c> (428 sin él, 412 si es viejo).</summary>
    private static RouteHandlerBuilder WithWriteContract<TRequest, TResponse>(this RouteHandlerBuilder route)
        where TRequest : notnull =>
        route.RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<TRequest>("application/json")
            .Produces<TResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static async Task<IResult> ListAsync(Guid tenantId, string? search, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListChatbotsQuery(tenantId, search), cancellationToken));

    // Sin Location: el editor abre con el chatbot que vuelve.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateChatbotRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new CreateChatbotCommand(
                    tenantId, request.Name, request.Description, request.Trigger, request.Keywords, request.ConnectionIds,
                    request.Source is null
                        ? null!
                        : new ChatbotSource(request.Source.Kind, request.Source.TemplateId, request.Source.ChatbotId, request.Source.Parameters)),
                cancellationToken));

    private static async Task<IResult> GetAsync(Guid tenantId, Guid chatbotId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetChatbotQuery(tenantId, chatbotId), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid chatbotId, UpdateChatbotRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateChatbotCommand(
                tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext), request.Name, request.Description, request.Trigger,
                request.Keywords, request.RestartKeyword, request.MaxAttempts, request.ConnectionIds),
            cancellationToken));

    private static async Task<IResult> SaveDraftAsync(
        Guid tenantId, Guid chatbotId, SaveDraftRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SaveChatbotDraftCommand(tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext), request.Flow, request.Layout),
            cancellationToken));

    private static async Task<IResult> ValidateAsync(Guid tenantId, Guid chatbotId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ValidateChatbotQuery(tenantId, chatbotId), cancellationToken));
}

/// <summary>§4.4: <c>source.kind</c> es <c>blank</c>, <c>template</c> o <c>duplicate</c>; los otros campos van según el tipo.</summary>
public sealed record ChatbotSourceRequest(string Kind, string? TemplateId, Guid? ChatbotId, IReadOnlyDictionary<string, string>? Parameters);

public sealed record CreateChatbotRequest(
    string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> ConnectionIds, ChatbotSourceRequest Source);

public sealed record UpdateChatbotRequest(
    string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts,
    IReadOnlyList<Guid> ConnectionIds);

/// <summary>D-C2: el <c>flow</c> se valida en el servidor; el <c>layout</c> viaja aparte y no se valida.</summary>
public sealed record SaveDraftRequest(JsonElement Flow, JsonElement Layout);
