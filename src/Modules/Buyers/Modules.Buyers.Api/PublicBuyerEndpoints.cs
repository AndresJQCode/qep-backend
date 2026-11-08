using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Store.Api;

namespace Modules.Buyers.Api;

/// <summary>
/// Rutas públicas de compradores, bajo el mismo prefijo y la misma clave del storefront que la tienda pública. Las llama sólo el
/// BFF del storefront, de servidor a servidor: sin sesión de la consola, sin cookie y sin CSRF. El <c>Cache-Control: no-store</c> de
/// todas sus respuestas lo pone <c>BuyerNoStoreMiddleware</c> (Bootstrapper), antes del limitador.
/// </summary>
public static class PublicBuyerEndpoints
{
    /// <summary>El header con el token de sesión del comprador. El backend no escribe cookies: el BFF guarda el token y lo reenvía.</summary>
    public const string BuyerSessionHeader = "X-Qep-Buyer-Session";

    /// <summary>Tope del cuerpo de cualquier ruta de compradores: un perfil con envío cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    private const int ReadChunkSize = 16 * 1024;

    /// <param name="readPolicy">Limitador del grupo (por IP del visitante); las rutas de credenciales lo reemplazan.</param>
    /// <param name="credentialsPolicy">Limitador de las rutas de credenciales: 10 por minuto, por ruta y por IP del visitante.</param>
    public static IEndpointRouteBuilder MapPublicBuyerEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string credentialsPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPolicy);

        var group = endpoints
            .MapGroup("/api/v1/public/stores/{host}/buyer")
            .WithTags("Buyers")
            .AllowAnonymous()
            .AddEndpointFilter<StorefrontKeyFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        group.MapPost("/register", RegisterAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<RegisterBuyerBody>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/email/verify", VerifyEmailAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<VerifyBuyerEmailBody>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/email/resend", ResendVerificationAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<ResendBuyerVerificationBody>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/session", LoginAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<LoginBuyerBody>("application/json")
            .Produces<BuyerSessionDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/session", GetSessionAsync)
            .Produces<BuyerDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapDelete("/session", LogoutAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPut("/profile", UpdateProfileAsync)
            .Accepts<BuyerProfileDto>("application/json")
            .Produces<BuyerDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/password", ChangePasswordAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<ChangeBuyerPasswordBody>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/password/forgot", ForgotPasswordAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<ForgotBuyerPasswordBody>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/password/reset", ResetPasswordAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<ResetBuyerPasswordBody>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    // Reemplaza el perfil guardado; no valida país ni ciudad contra la tienda (lo hace el pedido).
    private static async Task<IResult> UpdateProfileAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<BuyerProfileDto>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        return Results.Ok(
            await dispatcher.SendAsync(new UpdateBuyerProfileCommand(host, SessionToken(httpContext), body), cancellationToken));
    }

    // 204: la sesión actual sigue valiendo y las demás quedan revocadas. La contraseña actual mala es 422, nunca 401.
    private static async Task<IResult> ChangePasswordAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<ChangeBuyerPasswordBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new ChangeBuyerPasswordCommand(host, SessionToken(httpContext), body), cancellationToken);
        return Results.NoContent();
    }

    // La IP es opcional: sólo se guarda en la sesión. Un 401 de credenciales es siempre el mismo cuerpo.
    private static async Task<IResult> LoginAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<LoginBuyerBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        var visitorIp = StorefrontClient.TryGetDeclaredVisitorIp(httpContext, out var declared) ? declared : null;
        return Results.Ok(await dispatcher.SendAsync(new LoginBuyerCommand(host, visitorIp, body), cancellationToken));
    }

    private static async Task<IResult> GetSessionAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetBuyerSessionQuery(host, SessionToken(httpContext)), cancellationToken));

    // 204 siempre: sin una sesión válida en ese host no hace nada.
    private static async Task<IResult> LogoutAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new LogoutBuyerCommand(host, SessionToken(httpContext)), cancellationToken);
        return Results.NoContent();
    }

    private static string? SessionToken(HttpContext httpContext) =>
        httpContext.Request.Headers[BuyerSessionHeader].FirstOrDefault();

    // 202 exista o no la cuenta, esté o no verificada (anti-enumeración). Orden: IP -> host (404) -> cuerpo (422) -> Turnstile.
    private static async Task<IResult> ForgotPasswordAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var visitorIp = RequireVisitorIp(httpContext);
        var (body, rejection) = await ReadBodyAsync<ForgotBuyerPasswordBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new ForgotBuyerPasswordCommand(host, visitorIp, body), cancellationToken);
        return Results.Accepted();
    }

    // 204 y nada más: restablecer no abre sesión (revoca todas). Sin IP ni Turnstile: el token ya es el secreto.
    private static async Task<IResult> ResetPasswordAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<ResetBuyerPasswordBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new ResetBuyerPasswordCommand(host, body), cancellationToken);
        return Results.NoContent();
    }

    // 204 y nada más: verificar no abre sesión (el comprador ingresa después con su contraseña). Sin IP ni Turnstile: el token ya es
    // el secreto y el limitador de credenciales acota los intentos.
    private static async Task<IResult> VerifyEmailAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<VerifyBuyerEmailBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new VerifyBuyerEmailCommand(host, body), cancellationToken);
        return Results.NoContent();
    }

    // 202 exista o no la cuenta, esté o no verificada (anti-enumeración). Orden: IP → host (404) → cuerpo (422) → Turnstile.
    private static async Task<IResult> ResendVerificationAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var visitorIp = RequireVisitorIp(httpContext);
        var (body, rejection) = await ReadBodyAsync<ResendBuyerVerificationBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new ResendBuyerVerificationCommand(host, visitorIp, body), cancellationToken);
        return Results.Accepted();
    }

    // 202 exista o no la cuenta (anti-enumeración). Orden de chequeos: IP → host (404) → cuerpo (422) → consentimiento → Turnstile.
    private static async Task<IResult> RegisterAsync(
        string host,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var visitorIp = RequireVisitorIp(httpContext);
        var (body, rejection) = await ReadBodyAsync<RegisterBuyerBody>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        await dispatcher.SendAsync(new RegisterBuyerCommand(host, visitorIp, body), cancellationToken);
        return Results.Accepted();
    }

    // La IP del consentimiento y del limitador es la que declara el storefront con su clave, nunca la del par directo (que sería la
    // del servidor de la tienda).
    private static string RequireVisitorIp(HttpContext httpContext) =>
        StorefrontClient.TryGetDeclaredVisitorIp(httpContext, out var visitorIp)
            ? visitorIp
            : throw new BuyerDomainException(
                BuyerErrorCodes.VisitorIpRequired, "The storefront must forward the visitor IP for this request.");

    // El tope se aplica leyendo, igual que en el pedido de la tienda: RequestSizeLimitAttribute es sólo metadato que Kestrel convierte
    // en límite, y un servidor que no lo hace o un cuerpo sin Content-Length lo dejarían pasar. Un JSON ilegible o null es un 400
    // (el binder de minimal API lo dejaría salir como 500 por el manejador de excepciones).
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
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
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
                ["code"] = status == StatusCodes.Status413PayloadTooLarge ? "store.public.body_too_large" : "store.public.body_invalid",
            });
}
