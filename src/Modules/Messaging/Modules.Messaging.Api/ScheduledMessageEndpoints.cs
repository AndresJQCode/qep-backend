using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-11-09 «Contrato HTTP»: el mensaje programado de una conversación, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/messaging/conversations/{conversationId:guid}</c>. Leer pide <c>messaging.conversation.read</c>; crear y
/// gestionar, <c>messaging.conversation.manage</c> (por permiso, no por ser el asignado ni el autor: el handler revalida tenant, módulo y
/// membresía). Los errores salen por <c>ApiExceptionHandler</c>: 403 con código ante otro tenant, 404 con código para el id o la conversación
/// desconocidos, 409 y 412 de estado y versión, 422 del cuerpo y de las reglas de negocio. La versión viaja en el cuerpo
/// (<c>expectedVersion</c>), no en <c>If-Match</c>, como las campañas.
/// </summary>
internal static class ScheduledMessageEndpoints
{
    public static void MapScheduledMessageEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/conversations/{conversationId:guid}");

        group.MapGet("/scheduled-message", GetAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces<ScheduledMessageEnvelopeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/scheduled-messages", CreateAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<ScheduleMessageRequest>("application/json")
            .Produces<ScheduledMessageDto>(StatusCodes.Status201Created)
            .Produces<ScheduledMessageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Nota de voz en multipart. Mismo criterio que messages/audio: ruta propia, sin .Accepts (esa metadata haría que el routing
        // respondiera 415 a un cuerpo JSON antes del delegado, y el contrato pide 422 con errors.file), DisableAntiforgery con el CSRF por
        // X-Qep-Client en pie.
        group.MapPost("/scheduled-messages/audio", CreateVoiceNoteAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Produces<ScheduledMessageDto>(StatusCodes.Status201Created)
            .Produces<ScheduledMessageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPut("/scheduled-messages/{scheduledMessageId:guid}", UpdateAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<UpdateScheduledMessageRequest>("application/json")
            .Produces<ScheduledMessageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/scheduled-messages/{scheduledMessageId:guid}/cancel", CancelAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<ScheduledMessageVersionRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/scheduled-messages/{scheduledMessageId:guid}/send-now", SendNowAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<ScheduledMessageVersionRequest>("application/json")
            .Produces<ScheduledMessageDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/scheduled-messages/{scheduledMessageId:guid}/retry", RetryAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<ScheduledMessageVersionRequest>("application/json")
            .Produces<ScheduledMessageDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/scheduled-messages/{scheduledMessageId:guid}/discard", DiscardAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<ScheduledMessageVersionRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/scheduled-messages/{scheduledMessageId:guid}/media", GetMediaAsync)
            .RequireAuthorization(MessagingPermissions.ConversationRead)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetScheduledMessageQuery(tenantId, conversationId), cancellationToken));

    // 201 al guardar; 200 con la fila ya guardada si el clientId se repite. Sin Location: no hay GET de un programado por id.
    private static async Task<IResult> CreateAsync(
        Guid tenantId, Guid conversationId, ScheduleMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new CreateScheduledMessageCommand(
                tenantId, conversationId, request.ClientId, request.ScheduledAt, request.Text, request.UploadId, request.FileName, request.Caption),
            cancellationToken);
        return result.Created ? Results.Created((string?)null, result.Body) : Results.Ok(result.Body);
    }

    private static async Task<IResult> CreateVoiceNoteAsync(
        Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (form, rejection) = await MessagingEndpoints.ReadAudioFormAsync(httpContext, cancellationToken);
        if (rejection is not null)
        {
            return rejection;
        }

        var command = new CreateScheduledVoiceNoteCommand(
            tenantId,
            conversationId,
            MessagingEndpoints.ParseClientId(form!["clientId"]),
            ParseScheduledAt(form["scheduledAt"].ToString()),
            await MessagingEndpoints.ReadAudioAsync(form.Files.GetFile("file"), cancellationToken));
        var result = await dispatcher.SendAsync(command, cancellationToken);
        return result.Created ? Results.Created((string?)null, result.Body) : Results.Ok(result.Body);
    }

    // Ausente o ilegible: null, y el validador lo marca en errors.scheduledAt en vez de adivinar una hora.
    private static DateTimeOffset? ParseScheduledAt(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, UpdateScheduledMessageRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateScheduledMessageCommand(
                tenantId, conversationId, scheduledMessageId, request.ExpectedVersion, request.ScheduledAt, request.Text, request.Caption),
            cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, ScheduledMessageVersionRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new CancelScheduledMessageCommand(tenantId, conversationId, scheduledMessageId, request.ExpectedVersion), cancellationToken);
        return Results.NoContent();
    }

    // 202: el envío lo hace el worker en su próximo ciclo; la respuesta es la fila ya adelantada, no el mensaje enviado.
    private static async Task<IResult> SendNowAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, ScheduledMessageVersionRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Accepted(
            (string?)null,
            await dispatcher.SendAsync(
                new SendScheduledMessageNowCommand(tenantId, conversationId, scheduledMessageId, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> RetryAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, ScheduledMessageVersionRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Accepted(
            (string?)null,
            await dispatcher.SendAsync(
                new RetryScheduledMessageCommand(tenantId, conversationId, scheduledMessageId, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> DiscardAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, ScheduledMessageVersionRequest request, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DiscardScheduledMessageCommand(tenantId, conversationId, scheduledMessageId, request.ExpectedVersion), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMediaAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var media = await dispatcher.QueryAsync(
            new GetScheduledMessageMediaQuery(tenantId, conversationId, scheduledMessageId, httpContext.Request.Headers.Range.ToString()), cancellationToken);
        return media is null ? Results.NotFound() : MessagingEndpoints.WriteMedia(httpContext, media, scheduledMessageId);
    }
}

/// <summary>Spec 2026-11-09 «Contrato HTTP»: <c>text</c> o <c>uploadId</c> con su <c>fileName</c>, nunca los dos ni ninguno. Sin
/// <c>replyTo</c>: un programado no cita.</summary>
public sealed record ScheduleMessageRequest(Guid? ClientId, DateTimeOffset? ScheduledAt, string? Text, Guid? UploadId, string? FileName, string? Caption);

/// <summary><c>scheduledAt</c>, <c>text</c> y <c>caption</c> omitidos o <c>null</c> conservan lo guardado; <c>caption: ""</c> borra el pie.</summary>
public sealed record UpdateScheduledMessageRequest(long? ExpectedVersion, DateTimeOffset? ScheduledAt, string? Text, string? Caption);

/// <summary>El cuerpo de cancelar, «Enviar ya», reintentar y descartar: la versión que la pantalla estaba viendo.</summary>
public sealed record ScheduledMessageVersionRequest(long? ExpectedVersion);
