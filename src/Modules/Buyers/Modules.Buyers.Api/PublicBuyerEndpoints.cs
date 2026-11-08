using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Store.Api;

namespace Modules.Buyers.Api;

/// <summary>
/// Rutas públicas de compradores, bajo el mismo prefijo y la misma clave del storefront que la tienda pública. Las llama sólo el
/// BFF del storefront, de servidor a servidor: sin sesión de la consola, sin cookie y sin CSRF.
/// </summary>
public static class PublicBuyerEndpoints
{
    /// <summary>El header con el token de sesión del comprador. El backend no escribe cookies: el BFF guarda el token y lo reenvía.</summary>
    public const string BuyerSessionHeader = "X-Qep-Buyer-Session";

    /// <summary>Tope del cuerpo de cualquier ruta de compradores: un perfil con envío cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    /// <param name="readPolicy">Limitador del grupo (por IP del visitante); las rutas de credenciales lo reemplazan.</param>
    /// <param name="credentialsPolicy">Limitador de las rutas de credenciales: 10 por minuto, por ruta y por IP del visitante.</param>
    public static IEndpointRouteBuilder MapPublicBuyerEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string credentialsPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPolicy);

        // El filtro de no-store va primero (el más externo): así también lo llevan el 401 de la clave y los errores que salen por
        // excepción. Los 429 del limitador no pasan por acá (el limitador corre antes del endpoint) y salen sin cuerpo.
        var group = endpoints
            .MapGroup("/api/v1/public/stores/{host}/buyer")
            .WithTags("Buyers")
            .AllowAnonymous()
            .AddEndpointFilter(NoStoreAsync)
            .AddEndpointFilter<StorefrontKeyFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        group.MapPost("/register", RegisterAsync)
            .RequireRateLimiting(credentialsPolicy)
            .Accepts<RegisterBuyerBody>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    // OnStarting y no un set directo: el middleware de excepciones limpia la respuesta (cabeceras incluidas) antes de escribir el
    // problema, y las rutas que salen por excepción se quedarían sin la cabecera. Los callbacks corren al revés de como se
    // registran, así que éste (el primero) es el último en tocar la cabecera.
    private static ValueTask<object?> NoStoreAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.OnStarting(
            static state =>
            {
                ((HttpResponse)state).Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            },
            context.HttpContext.Response);
        return next(context);
    }

    // 202 exista o no la cuenta (anti-enumeración). Orden de chequeos: IP → host (404) → cuerpo (422) → consentimiento → Turnstile.
    private static async Task<IResult> RegisterAsync(
        string host,
        RegisterBuyerBody body,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var visitorIp = RequireVisitorIp(httpContext);
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
}
