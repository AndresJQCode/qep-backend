using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-30 §5.2: las rutas de WhatsApp de la API externa, bajo <c>/api/v1/external/whatsapp</c>. El grupo padre (en
/// <c>src/Api</c>) fija el esquema <c>ApiKey</c> y el filtro de la capacidad <c>developer</c>; cada ruta agrega aquí su scope, que es la
/// capa del endpoint, y su handler lo revalida (<see cref="ExternalApiAccess"/>).
/// </summary>
public static class ExternalWhatsAppEndpoints
{
    public static RouteGroupBuilder MapExternalWhatsAppEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var whatsapp = group.MapGroup("/whatsapp");

        whatsapp.MapGet("/connections", ListConnectionsAsync)
            .RequireAuthorization(RequireTemplatesSend)
            .Produces<IReadOnlyList<ExternalConnectionDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        whatsapp.MapGet("/connections/{connectionId:guid}/templates", ListTemplatesAsync)
            .RequireAuthorization(RequireTemplatesSend)
            .Produces<IReadOnlyList<ExternalTemplateDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        whatsapp.MapPost("/messages", SendMessageAsync)
            .RequireAuthorization(RequireTemplatesSend)
            .Accepts<ExternalSendMessageRequest>("application/json")
            .Produces<ExternalSendMessageResponse>(StatusCodes.Status201Created)
            .Produces<ExternalSendMessageResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    private static void RequireTemplatesSend(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(ApiKeyDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ApiKeyClaimTypes.Scope, MessagingScopes.TemplatesSend);

    private static async Task<IResult> ListConnectionsAsync(IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListExternalConnectionsQuery(), cancellationToken));

    private static async Task<IResult> ListTemplatesAsync(
        Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListExternalTemplatesQuery(connectionId), cancellationToken));

    private static async Task<IResult> SendMessageAsync(HttpRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken);
        var template = body.Template;
        var result = await dispatcher.SendAsync(
            new SendExternalTemplateCommand(body.ClientId, body.ConnectionId, body.To, template?.Name, template?.Language, template?.Parameters),
            cancellationToken);
        var response = new ExternalSendMessageResponse(result.MessageId, result.ConversationId, result.Status);
        return result.Replayed ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>El cuerpo se lee a mano y no por parámetro: un integrador real manda JSON roto o un <c>parameters</c> numérico, y el enlace
    /// automático respondería un 400 sin <c>code</c>.</summary>
    private static async Task<ExternalSendMessageRequest> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ExternalSendMessageRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ExternalSendMessageRequest>(request.Body, JsonSerializerOptions.Web, cancellationToken);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body ?? throw new ValidationException([new ValidationFailure("body", "El cuerpo debe ser JSON válido, con parameters como texto.")]);
    }
}

/// <summary>Spec 2026-10-30 §5.2. Todo es nullable y texto: el validador responde campo por campo.</summary>
public sealed record ExternalSendMessageRequest(string? ClientId, string? ConnectionId, string? To, ExternalTemplateRequest? Template);

public sealed record ExternalTemplateRequest(string? Name, string? Language, IReadOnlyDictionary<string, string?>? Parameters);

public sealed record ExternalSendMessageResponse(Guid MessageId, Guid ConversationId, string Status);
