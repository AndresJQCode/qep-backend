using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Api;

/// <summary>
/// El formulario público de Soporte (spec §9, plan F3 a F6): rutas anónimas que la página de <c>soporte.lulocrm.com</c> llama directo
/// desde el navegador, sin BFF. Sin sesión y sin CSRF (excepción por prefijo en Bootstrapper, <c>SupportPublicPaths</c>, que una prueba
/// ata a <see cref="GroupPattern"/>); con la política CORS propia sin credenciales (<see cref="CorsPolicyName"/>), el tope del cuerpo
/// de 64 KB y los limitadores por IP del visitante. El <c>Cache-Control: no-store</c> de toda respuesta lo pone
/// <c>BuyerNoStoreMiddleware</c>, antes del limitador. Ningún handler del grupo lee el contexto de ejecución ni la cookie.
/// </summary>
public static class PublicSupportEndpoints
{
    /// <summary>El patrón del grupo. Bootstrapper duplica el prefijo (<c>SupportPublicPaths.Prefix</c>): <c>SupportPublicPathsTests</c>
    /// los ata.</summary>
    public const string GroupPattern = "/api/v1/public/support/{tenantSlug}";

    /// <summary>La política CORS del grupo, registrada en Bootstrapper (<c>CorsServiceCollectionExtensions</c>).</summary>
    public const string CorsPolicyName = "support-public";

    /// <summary>Tope del cuerpo de cualquier ruta del grupo: un radicado con todos los campos cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    private const int ReadChunkSize = 16 * 1024;

    /// <param name="readPolicy">Configuración y geografía: el limitador del grupo.</param>
    /// <param name="submitPolicy">Radicar y reenviar el PIN.</param>
    /// <param name="lookupPolicy">Consultar un ticket.</param>
    /// <param name="uploadPolicy">Abrir y completar la subida de un adjunto (plan F33).</param>
    public static IEndpointRouteBuilder MapPublicSupportEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string submitPolicy, string lookupPolicy, string uploadPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadPolicy);

        var group = endpoints
            .MapGroup(GroupPattern)
            .WithTags("Support public")
            .AllowAnonymous()
            .RequireCors(CorsPolicyName)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        group.MapGet("/form", GetFormAsync)
            .Produces<SupportPublicFormDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Owner, 2026-11-15 (E1): la página de consulta se dibuja con esto aunque el formulario no esté publicado.
        group.MapGet("/consult", GetConsultAsync)
            .Produces<SupportPublicConsultDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/geography/departments", ListDepartmentsAsync)
            .Produces<SupportPublicGeoListDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/geography/departments/{departmentId:guid}/cities", ListCitiesAsync)
            .Produces<SupportPublicGeoListDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/tickets", FileAsync)
            .RequireRateLimiting(submitPolicy)
            .Accepts<SupportPublicFileRequest>("application/json")
            .Produces<SupportPublicFiledDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/tickets/lookup", LookupAsync)
            .RequireRateLimiting(lookupPolicy)
            .Accepts<SupportPublicLookupRequest>("application/json")
            .Produces<SupportPublicTicketDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Fase 4b (H20): calificar desde la consulta, con la misma verificación y el mismo limitador que consultar.
        group.MapPost("/tickets/rating", RateAsync)
            .RequireRateLimiting(lookupPolicy)
            .Accepts<SupportPublicRatingRequest>("application/json")
            .Produces<SupportPublicTicketDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Fase 4b (H19, Q-12): el enlace del correo. Sin Turnstile; el token va en el cuerpo, nunca en la ruta, con el limitador de la
        // consulta.
        group.MapPost("/surveys/lookup", LookupSurveyAsync)
            .RequireRateLimiting(lookupPolicy)
            .Accepts<SupportPublicSurveyTokenRequest>("application/json")
            .Produces<SupportPublicSurveyDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/surveys/answer", AnswerSurveyAsync)
            .RequireRateLimiting(lookupPolicy)
            .Accepts<SupportPublicSurveyAnswerRequest>("application/json")
            .Produces<SupportPublicSurveyDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/tickets/resend-pin", ResendPinAsync)
            .RequireRateLimiting(submitPolicy)
            .Accepts<SupportPublicResendPinRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Plan F33: los adjuntos. Sin Turnstile (el token es de un solo uso y se gasta al radicar), con su propio limitador. El archivo
        // no pasa por acá: el navegador lo sube directo al bucket con la URL firmada.
        group.MapPost("/attachments", OpenAttachmentAsync)
            .RequireRateLimiting(uploadPolicy)
            .Accepts<SupportPublicAttachmentRequest>("application/json")
            .Produces<SupportPublicAttachmentSessionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/attachments/{fileId:guid}/complete", CompleteAttachmentAsync)
            .RequireRateLimiting(uploadPolicy)
            .Accepts<SupportPublicAttachmentCompleteRequest>("application/json")
            .Produces<SupportPublicAttachmentStatusDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    // El uploadKey sólo viaja en esta respuesta y en el cuerpo de completar y radicar, nunca en una URL (plan F32).
    private static async Task<IResult> OpenAttachmentAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicAttachmentRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        var session = await dispatcher.SendAsync(
            new OpenPublicAttachmentCommand(tenantSlug, body.Name, body.MimeType, body.SizeBytes), cancellationToken);
        return Results.Created((string?)null, session);
    }

    private static async Task<IResult> CompleteAttachmentAsync(
        string tenantSlug, Guid fileId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicAttachmentCompleteRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(await dispatcher.SendAsync(
            new CompletePublicAttachmentCommand(tenantSlug, fileId, body.UploadKey), cancellationToken));
    }

    private static async Task<IResult> GetFormAsync(
        string tenantSlug, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPublicFormQuery(tenantSlug), cancellationToken));

    private static async Task<IResult> GetConsultAsync(
        string tenantSlug, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPublicConsultQuery(tenantSlug), cancellationToken));

    private static async Task<IResult> ListDepartmentsAsync(
        string tenantSlug, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPublicSupportDepartmentsQuery(tenantSlug), cancellationToken));

    private static async Task<IResult> ListCitiesAsync(
        string tenantSlug, Guid departmentId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPublicSupportCitiesQuery(tenantSlug, departmentId), cancellationToken));

    // El PIN sólo viaja en esta respuesta, una vez (plan F9 y el contrato).
    private static async Task<IResult> FileAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicFileRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        var filed = await dispatcher.SendAsync(
            FilePublicTicketCommand.From(tenantSlug, VisitorIp(httpContext), body), cancellationToken);
        return Results.Created((string?)null, filed);
    }

    // La IP del visitante: RemoteIpAddress, que UseForwardedHeaders ya tomó de X-Real-IP detrás del ingress de confianza. Va a Turnstile
    // y, sólo en el camino con datos, a las aceptaciones del aviso de privacidad y de los términos (legal_acceptances.visitor_ip); un
    // anónimo no la guarda en ninguna fila. Sin IP (sólo en un TestServer sin par) queda un valor fijo, para que esas filas no se pierdan.
    private static string VisitorIp(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // El PIN viaja en el cuerpo, nunca en la URL (plan F9).
    private static async Task<IResult> LookupAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicLookupRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(await dispatcher.SendAsync(
            new LookupPublicTicketCommand(tenantSlug, VisitorIp(httpContext), body.Number, body.Pin, body.TurnstileToken),
            cancellationToken));
    }

    // Fase 4b (H20): el PIN viaja en el cuerpo, como en la consulta.
    private static async Task<IResult> RateAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicRatingRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(await dispatcher.SendAsync(
            new RatePublicTicketCommand(
                tenantSlug, VisitorIp(httpContext), body.Number, body.Pin, body.TurnstileToken, body.Rating, body.Comment),
            cancellationToken));
    }

    // Fase 4b (H19): el token en el cuerpo, nunca en la ruta (no queda en los logs de acceso).
    private static async Task<IResult> LookupSurveyAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicSurveyTokenRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(await dispatcher.QueryAsync(new LookupPublicSurveyQuery(tenantSlug, body.Token), cancellationToken));
    }

    private static async Task<IResult> AnswerSurveyAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicSurveyAnswerRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(await dispatcher.SendAsync(
            new AnswerPublicSurveyCommand(tenantSlug, body.Token, body.Rating, body.Comment), cancellationToken));
    }

    // Siempre 202 vacío (plan F10, Review Focus 4): la respuesta no dice si el radicado existe ni si salió algo.
    private static async Task<IResult> ResendPinAsync(
        string tenantSlug, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicResendPinRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        _ = await dispatcher.SendAsync(
            new ResendPublicPinCommand(tenantSlug, VisitorIp(httpContext), body.Number, body.TurnstileToken), cancellationToken);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    // Copia del lector de PublicBuyerEndpoints: el tope se aplica leyendo, porque RequestSizeLimitAttribute es sólo metadato que
    // Kestrel convierte en límite, y un servidor que no lo hace o un cuerpo sin Content-Length lo dejarían pasar. Un JSON ilegible o
    // null es un 400 (el binder de minimal API lo dejaría salir como 500 por el manejador de excepciones).
    private static async Task<(T? Body, IResult? Rejection)> ReadBodyAsync<T>(
        HttpContext httpContext, CancellationToken cancellationToken)
        where T : class
    {
        var stream = httpContext.Request.Body;
        if (httpContext.Request.ContentLength is { } declared && declared > MaxRequestBodyBytes)
        {
            return (null, Rejected(StatusCodes.Status413PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxRequestBodyBytes)
                {
                    return (null, Rejected(StatusCodes.Status413PayloadTooLarge));
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
        {
            return (null, Rejected(exception.StatusCode));
        }

        var options = httpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        try
        {
            var body = JsonSerializer.Deserialize<T>(buffer.ToArray(), options);
            return body is null ? (null, Rejected(StatusCodes.Status400BadRequest)) : (body, null);
        }
        catch (JsonException)
        {
            return (null, Rejected(StatusCodes.Status400BadRequest));
        }
    }

    private static IResult Rejected(int status) =>
        Results.Problem(
            statusCode: status,
            title: "The request body is not acceptable.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = status == StatusCodes.Status413PayloadTooLarge
                    ? SupportErrorCodes.PublicBodyTooLarge
                    : SupportErrorCodes.PublicBodyInvalid,
            });
}
