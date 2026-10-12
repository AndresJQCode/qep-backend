using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Spec 2026-10-11 §6.1: 16 MB de audio más lo que ocupa el multipart (límites, cabeceras de cada parte y
    /// los dos campos de texto). Sólo para esta ruta: el resto de la API conserva el suyo (§9).</summary>
    internal const long AudioBodyMaxBytes = 17L * 1024 * 1024;

    /// <summary>Lista, detalle e hilo (Task 14), búsqueda (Task 15) y envío (Task 16), nota de voz (spec 2026-10-11), leído, resolver y reabrir (Task 17)
    /// y el medio (Task 18), todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta; cada endpoint su política (spec 2026-10-09 §6.4). Los handlers revalidan
        // tenant, permiso y módulo, y responden 403 con código, nunca 404, ante otro tenant o módulo apagado.
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/messaging").WithTags("Messaging");

        group.MapGet("/conversations", ListConversationsAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/conversations/{conversationId:guid}", GetConversationAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/conversations/{conversationId:guid}/messages", ListMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<MessagePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-09 §8.3: envío de texto, idempotente por clientId.
        group.MapPost("/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<SendMessageRequest>("application/json")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-11 §5.2: plantillas. Las conexiones de WhatsApp activas (con el permiso de la bandeja, no el de Integrations) y el
        // catálogo aprobado de una de ellas.
        group.MapGet("/connections", ListConnectionsAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<IReadOnlyList<MessagingConnectionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/connections/{connectionId:guid}/templates", ListTemplatesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<IReadOnlyList<MessageTemplateDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/conversations/{conversationId:guid}/messages/template", SendTemplateAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<SendTemplateRequest>("application/json")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 201 con la conversación creada; 200 si el cliente ya tenía una en esa conexión y se envió ahí.
        group.MapPost("/conversations", StartConversationAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<StartConversationRequest>("application/json")
            .Produces<StartedConversationDto>(StatusCodes.Status201Created)
            .Produces<StartedConversationDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-11 §4.1: nota de voz en multipart. Ruta propia, para que el Accepts del texto siga en JSON.
        // DisableAntiforgery con el criterio de CustomerEndpoints (/import); el CSRF por X-Qep-Client sigue aplicando.
        // Sin .Accepts("multipart/form-data") a propósito: esa metadata activa el AcceptsMatcherPolicy del routing, que
        // responde 415 a un cuerpo JSON antes de entrar al delegado, y el contrato pide 422 con errors.file y errors.clientId.
        group.MapPost("/conversations/{conversationId:guid}/messages/audio", SendAudioMessageAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        // Spec 2026-10-09 §8.4: sin If-Match; el acuse a Meta es best effort y la respuesta siempre 204.
        group.MapPost("/conversations/{conversationId:guid}/read", MarkReadAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Spec 2026-10-09 §8.5: por el agregado con If-Match (428 sin él, 412 con versión vieja).
        group.MapPost("/conversations/{conversationId:guid}/resolve", ResolveAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/reopen", ReopenAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-10-10 §5.1 y §8.4: asignación con If-Match (428 sin él, 412 con versión vieja); 200 ConversationSummary.
        group.MapPost("/conversations/{conversationId:guid}/take", TakeAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/transfer", TransferAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<TransferConversationRequest>("application/json")
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/conversations/{conversationId:guid}/release", ReleaseAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ConversationSummary>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/messages/search", SearchMessagesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<SearchPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-09 §8.6: el medio copiado, servido con la sesión. 404 sin código mientras no esté.
        group.MapGet("/media/{messageId:guid}", GetMediaAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // Spec 2026-10-10 §5.1 (D-A1): a quién se le puede transferir.
        group.MapGet("/assignees", ListAssigneesAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<AssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Spec 2026-10-11 §6.2: campañas, con su propio permiso (decisión 16).
        group.MapCampaignEndpoints();
        // Spec 2026-10-12 §3.6: respuestas rápidas.
        group.MapQuickReplyEndpoints();

        return endpoints;
    }

    /// <summary>§8.6, «Servir»: nosniff + CSP sandbox siempre; inline sólo para imagen (salvo SVG), audio y
    /// video; attachment con nombre para lo demás, para que un HTML o un SVG no se ejecute en el origen de
    /// la API. 404 sin código mientras no esté copiado: la URL es opaca y no hay nada que el cliente corrija.</summary>
    private static async Task<IResult> GetMediaAsync(
        Guid tenantId, Guid messageId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var media = await dispatcher.QueryAsync(new GetMediaQuery(tenantId, messageId), cancellationToken);
        if (media is null)
        {
            return Results.NotFound();
        }

        var headers = httpContext.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        headers.CacheControl = "private, max-age=3600";
        headers.ContentDisposition = ContentDispositionFor(media.ContentType, media.FileName, messageId);
        headers.ContentLength = media.Length;
        return Results.Stream(media.Content, media.ContentType);
    }

    internal static string ContentDispositionFor(string contentType, string? fileName, Guid messageId)
    {
        if (IsInlineSafe(contentType))
        {
            return "inline";
        }

        // RFC 6266 con filename* (RFC 5987) para los nombres con tildes; las comillas y los controles los escapa SetHttpFileName.
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(string.IsNullOrWhiteSpace(fileName) ? messageId.ToString("N") : fileName);
        return disposition.ToString();
    }

    private static bool IsInlineSafe(string contentType) =>
        (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
        || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> ListConversationsAsync(
        Guid tenantId, string? status, string? search, int? page, int? pageSize, string? assigned, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConversationsQuery(tenantId, status, search, page, pageSize, assigned), cancellationToken));

    private static async Task<IResult> ListAssigneesAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAssigneesQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetConversationAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConversationQuery(tenantId, conversationId), cancellationToken));

    private static async Task<IResult> ListMessagesAsync(
        Guid tenantId, Guid conversationId, int? limit, Guid? before, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListMessagesQuery(tenantId, conversationId, limit, before), cancellationToken));

    private static async Task<IResult> SearchMessagesAsync(
        Guid tenantId, string? q, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, int? limit, Guid? before,
        IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new SearchMessagesQuery(tenantId, q, conversationId, from, to, limit, before), cancellationToken));

    private static async Task<IResult> SendMessageAsync(
        Guid tenantId, Guid conversationId, SendMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var message = await dispatcher.SendAsync(new SendMessageCommand(tenantId, conversationId, request.ClientId, request.Text, request.ReplyTo), cancellationToken);
        // Sin Location: no hay GET de un mensaje suelto, y apuntar a la colección del hilo confundiría.
        return Results.Created((string?)null, message);
    }

    private static async Task<IResult> ListConnectionsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListMessagingConnectionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListTemplatesAsync(
        Guid tenantId, Guid connectionId, bool? refresh, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTemplatesQuery(tenantId, connectionId, refresh ?? false), cancellationToken));

    private static async Task<IResult> SendTemplateAsync(
        Guid tenantId, Guid conversationId, SendTemplateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var message = await dispatcher.SendAsync(
            new SendTemplateMessageCommand(tenantId, conversationId, request.ClientId, request.Name, request.Language, request.Parameters), cancellationToken);
        // Sin Location, igual que el texto.
        return Results.Created((string?)null, message);
    }

    private static async Task<IResult> StartConversationAsync(
        Guid tenantId, StartConversationRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new StartConversationCommand(
                tenantId, request.ClientId, request.ConnectionId, request.CustomerId, request.Template?.Name, request.Template?.Language, request.Template?.Parameters),
            cancellationToken);
        return result.Created ? Results.Created((string?)null, result.Body) : Results.Ok(result.Body);
    }

    /// <summary>
    /// Spec 2026-10-11 §6.1. El form se lee a mano y no con un <c>IFormFile</c> en la firma: el binder lo leería entero
    /// antes de entrar acá, con el tope global, y fijar el del endpoint ya no serviría. Primero el tope, después el form.
    /// Un <c>Content-Length</c> declarado de más se corta sin leer; uno chunked lo corta <c>MultipartBodyLengthLimit</c>.
    /// Lo que falte o no parsee llega como <c>null</c> (o <c>Guid.Empty</c> en <c>replyTo</c>) y lo marca el validador
    /// con su clave: un form sin <c>file</c> o un cuerpo que no es multipart es 422, no 415.
    /// </summary>
    private static async Task<IResult> SendAudioMessageAsync(
        Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = AudioBodyMaxBytes;
        }

        var request = httpContext.Request;
        if (request.ContentLength > AudioBodyMaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        IFormCollection form;
        try
        {
            form = request.HasFormContentType
                ? await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = AudioBodyMaxBytes }, cancellationToken)
                : FormCollection.Empty;
        }
        catch (InvalidDataException)
        {
            // MultipartBodyLengthLimit superado en un cuerpo sin Content-Length.
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (BadHttpRequestException exception)
        {
            // Lo que Kestrel corta al leer (el tope superado, o un cuerpo más corto que su Content-Length): un request malo,
            // no una falla nuestra. Sin esto ApiExceptionHandler lo volvería un 500 (mismo criterio que WebhookBodyReader).
            return Results.StatusCode(exception.StatusCode);
        }

        var command = new SendAudioMessageCommand(
            tenantId, conversationId, ParseClientId(form["clientId"]), await ReadAudioAsync(form.Files.GetFile("file"), cancellationToken), ParseReplyTo(form["replyTo"]));
        var message = await dispatcher.SendAsync(command, cancellationToken);
        // Sin Location, igual que el texto.
        return Results.Created((string?)null, message);
    }

    // ≤ 17 MB por el tope del cuerpo; el validador decide si cabe en los 16 MB de Meta. Un solo arreglo del tamaño del
    // archivo: pasar por un MemoryStream y después ToArray() duplicaba hasta 16 MB en el LOH por request.
    internal static async Task<byte[]?> ReadAudioAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return null;
        }

        var audio = new byte[checked((int)Math.Min(file.Length, AudioBodyMaxBytes))];
        await using var content = file.OpenReadStream();
        await content.ReadExactlyAsync(audio, cancellationToken);
        return audio;
    }

    private static Guid? ParseClientId(StringValues value) => Guid.TryParse(value.ToString(), out var id) ? id : null;

    /// <summary>Ausente o vacío: sin cita. Presente y no GUID: <c>Guid.Empty</c>, para que el validador lo marque en
    /// <c>errors.replyTo</c> en vez de ignorar la cita en silencio.</summary>
    private static Guid? ParseReplyTo(StringValues value) =>
        StringValues.IsNullOrEmpty(value) ? null : Guid.TryParse(value.ToString(), out var id) ? id : Guid.Empty;

    private static async Task<IResult> MarkReadAsync(Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new MarkConversationReadCommand(tenantId, conversationId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ResolveAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ResolveConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ReopenAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReopenConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> TakeAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TakeConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> TransferAsync(
        Guid tenantId, Guid conversationId, TransferConversationRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TransferConversationCommand(tenantId, conversationId, request.MemberId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ReleaseAsync(Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReleaseConversationCommand(tenantId, conversationId, RequireVersion(httpContext)), cancellationToken));

    // Copia de IntegrationsEndpoints: mismo contrato que /pos y /orders-export-layout; sin If-Match 428,
    // vieja 412 (en el handler).
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded conversation version is required.");

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

/// <summary>§5.3: <c>clientId</c> lo genera la pantalla por intento de envío; un reintento con el mismo no duplica.</summary>
public sealed record SendMessageRequest(Guid? ClientId, string? Text, Guid? ReplyTo = null);

/// <summary>Spec 2026-10-10 §5.1.</summary>
public sealed record TransferConversationRequest(Guid? MemberId);

/// <summary>Spec 2026-10-11 §5.2: <c>parameters</c> es un objeto <c>{ clave: valor }</c> con exactamente las claves de la plantilla.</summary>
public sealed record SendTemplateRequest(Guid? ClientId, string? Name, string? Language, Dictionary<string, string?>? Parameters);

/// <summary>Spec 2026-10-11 §5.2: <c>POST /conversations</c>.</summary>
public sealed record StartConversationRequest(Guid? ClientId, Guid? ConnectionId, Guid? CustomerId, StartConversationTemplateRequest? Template);

public sealed record StartConversationTemplateRequest(string? Name, string? Language, Dictionary<string, string?>? Parameters);
