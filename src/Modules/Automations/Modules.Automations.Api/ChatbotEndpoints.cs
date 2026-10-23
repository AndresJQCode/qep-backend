using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Modules.Automations.Application;

namespace Modules.Automations.Api;

/// <summary>Spec 2026-10-21 §4.4: lista, creación, lectura, edición, borrador, validación, publicación, pausa, reanudación, duplicado y
/// borrado del chatbot. Leer y validar con <c>automations.chatbot.read</c>; escribir con <c>automations.chatbot.manage</c>, con
/// <c>If-Match</c> sobre un chatbot existente (menos duplicar, que no lo cambia).</summary>
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

        chatbots.MapPost("/{chatbotId:guid}/publish", PublishAsync)
            .WithActionContract()
            .Accepts<PublishChatbotRequest>("application/json")
            .Produces<ChatbotEditorDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        chatbots.MapPost("/{chatbotId:guid}/pause", PauseAsync)
            .WithActionContract()
            .Produces<ChatbotEditorDto>();

        chatbots.MapPost("/{chatbotId:guid}/resume", ResumeAsync)
            .WithActionContract()
            .Produces<ChatbotEditorDto>();

        chatbots.MapPost("/{chatbotId:guid}/duplicate", DuplicateAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Produces<ChatbotEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        chatbots.MapGet("/{chatbotId:guid}/export", ExportAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Produces<ChatbotExportDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        chatbots.MapPost("/import", ImportAsync)
            .RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .Accepts<ImportChatbotRequest>("application/json")
            .Produces<ChatbotEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        chatbots.MapDelete("/{chatbotId:guid}", DeleteAsync)
            .WithActionContract()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>Una acción sin cuerpo sobre un chatbot existente (publicar, pausar, reanudar, borrar): <c>If-Match</c> como en
    /// <see cref="WithWriteContract{TRequest, TResponse}"/>.</summary>
    private static RouteHandlerBuilder WithActionContract(this RouteHandlerBuilder route) =>
        route.RequireAuthorization(AutomationsPermissions.ChatbotManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

    /// <summary><c>takeOverConnectionIds</c> (opcional) quita esas conexiones al otro chatbot publicado que las tenga; lo que no esté ocupado o
    /// no esté en <c>connectionIds</c> se ignora, y en un borrador se ignora entera (spec 2026-10-23 D-X1, D-X4).</summary>
    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid chatbotId, UpdateChatbotRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateChatbotCommand(
                tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext), request.Name, request.Description, request.Trigger,
                request.Keywords, request.RestartKeyword, request.MaxAttempts, request.ConnectionIds, request.TakeOverConnectionIds ?? []),
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
        var (draft, failure) = await ReadBoundedJsonAsync<SaveDraftRequest>(httpContext, ChatbotLimits.DraftBodyMaxBytes, cancellationToken);
        if (draft is null)
        {
            return failure!;
        }

        return Results.Ok(await dispatcher.SendAsync(
            new SaveChatbotDraftCommand(tenantId, chatbotId, version, draft.Flow, draft.Layout),
            cancellationToken));
    }

    /// <summary>Lee el cuerpo JSON con el tope <paramref name="maxBytes"/> (<see cref="ChatbotLimits.DraftBodyMaxBytes"/> para el borrador,
    /// <see cref="ChatbotLimits.ImportBodyMaxBytes"/> para importar) y lo deserializa. Devuelve el resultado HTTP a responder si no se puede.</summary>
    private static async Task<(T? Body, IResult? Failure)> ReadBoundedJsonAsync<T>(HttpContext httpContext, int maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        var request = httpContext.Request;
        if (request.ContentLength > maxBytes)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        if (!request.HasJsonContentType())
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        var (body, status) = await ReadBoundedAsync(request.Body, maxBytes, cancellationToken);
        if (body is null)
        {
            return (null, Results.StatusCode(status));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web) is { } value
                ? (value, null)
                : (null, Results.StatusCode(StatusCodes.Status400BadRequest));
        }
        catch (JsonException)
        {
            // Lo mismo que respondería el binder con un JSON roto.
            return (null, Results.StatusCode(StatusCodes.Status400BadRequest));
        }
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

    /// <summary>El cuerpo es opcional (spec 2026-10-23 D-X1): el frontend publica sin cuerpo cuando no hay conexiones que mover. Las de
    /// <c>takeOverConnectionIds</c> se le quitan al otro chatbot publicado que las tenga; lo que no esté ocupado o no sea del chatbot se ignora.</summary>
    private static async Task<IResult> PublishAsync(
        Guid tenantId, Guid chatbotId, PublishChatbotRequest? request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PublishChatbotCommand(tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext), request?.TakeOverConnectionIds ?? []),
            cancellationToken));

    private static async Task<IResult> PauseAsync(
        Guid tenantId, Guid chatbotId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PauseChatbotCommand(tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ResumeAsync(
        Guid tenantId, Guid chatbotId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResumeChatbotCommand(tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext)), cancellationToken));

    // Sin Location, como crear: el editor abre con la copia que vuelve.
    private static async Task<IResult> DuplicateAsync(Guid tenantId, Guid chatbotId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created((string?)null, await dispatcher.SendAsync(new DuplicateChatbotCommand(tenantId, chatbotId), cancellationToken));

    private static async Task<IResult> ExportAsync(Guid tenantId, Guid chatbotId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ExportChatbotQuery(tenantId, chatbotId), cancellationToken));

    /// <summary>D-C9: el archivo importado se lee con el mismo tope del borrador, a mano, por la misma razón que <see cref="SaveDraftAsync"/>.
    /// Sin Location, como crear.</summary>
    private static async Task<IResult> ImportAsync(
        Guid tenantId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (request, failure) = await ReadBoundedJsonAsync<ImportChatbotRequest>(httpContext, ChatbotLimits.ImportBodyMaxBytes, cancellationToken);
        return request is null
            ? failure!
            : Results.Created((string?)null, await dispatcher.SendAsync(new ImportChatbotCommand(tenantId, request), cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid chatbotId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteChatbotCommand(tenantId, chatbotId, AutomationsEndpoints.RequireVersion(httpContext)), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>§4.4: <c>source.kind</c> es <c>blank</c>, <c>template</c> o <c>duplicate</c>; los otros campos van según el tipo.</summary>
public sealed record ChatbotSourceRequest(string Kind, string? TemplateId, Guid? ChatbotId, IReadOnlyDictionary<string, string>? Parameters);

public sealed record CreateChatbotRequest(
    string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> ConnectionIds, ChatbotSourceRequest Source);

/// <summary><c>TakeOverConnectionIds</c>: conexiones que se le quitan a otro chatbot publicado; en un borrador se ignora (spec 2026-10-23 D-X1, D-X4).</summary>
public sealed record UpdateChatbotRequest(
    string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts,
    IReadOnlyList<Guid> ConnectionIds, IReadOnlyList<Guid>? TakeOverConnectionIds = null);

/// <summary>Cuerpo opcional de <c>publish</c>: conexiones que se le quitan a otro chatbot publicado (spec 2026-10-23 D-X1).</summary>
public sealed record PublishChatbotRequest(IReadOnlyList<Guid>? TakeOverConnectionIds);

/// <summary>D-C2: el <c>flow</c> se valida en el servidor; el <c>layout</c> viaja aparte y no se valida.</summary>
public sealed record SaveDraftRequest(JsonElement Flow, JsonElement Layout);
