using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
            .WithWriteContract<SaveDraftRequest, DraftSavedDto>()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

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

    /// <summary>
    /// §4.4: el cuerpo del borrador se lee a mano y con tope, como <c>MessagingEndpoints.SendAudioMessageAsync</c>: con
    /// <see cref="SaveDraftRequest"/> en la firma, el binder lo leería entero (con el tope global de 30 MB) antes de entrar acá. Un
    /// <c>Content-Length</c> de más se corta sin leer; uno chunked, al pasar el tope. Los 512 KB y 256 KB de cada parte los mide el
    /// validador con su clave.
    /// </summary>
    private static async Task<IResult> SaveDraftAsync(
        Guid tenantId, Guid chatbotId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        // Antes que el cuerpo: sin If-Match no vale la pena leerlo.
        var version = AutomationsEndpoints.RequireVersion(httpContext);
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = ChatbotLimits.DraftBodyMaxBytes;
        }

        var request = httpContext.Request;
        if (request.ContentLength > ChatbotLimits.DraftBodyMaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var (body, status) = await ReadBoundedAsync(request.Body, ChatbotLimits.DraftBodyMaxBytes, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(status);
        }

        SaveDraftRequest? draft;
        try
        {
            draft = JsonSerializer.Deserialize<SaveDraftRequest>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            // Lo mismo que respondería el binder con un JSON roto.
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (draft is null)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        return Results.Ok(await dispatcher.SendAsync(
            new SaveChatbotDraftCommand(tenantId, chatbotId, version, draft.Flow, draft.Layout),
            cancellationToken));
    }

    private static async Task<(byte[]? Body, int Status)> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (null, StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
        {
            // Lo que Kestrel corta al leer (el tope en chunked, 413; un cuerpo más corto que su Content-Length, 400): un request malo,
            // no una falla nuestra, que el ApiExceptionHandler volvería 500 (mismo criterio que WebhookBodyReader de Messaging).
            return (null, exception.StatusCode);
        }

        return (buffer.ToArray(), StatusCodes.Status200OK);
    }

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
