using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bootstrapper.Csrf;

public static class CsrfApplicationBuilderExtensions
{
    public static IApplicationBuilder UseQepCsrfProtection(this IApplicationBuilder app) =>
        app.UseMiddleware<RequireCsrfHeaderMiddleware>();
}

// Defensa CSRF mínima para la sesión autenticada por cookie (ver el ADR de la cookie
// de sesión). Todo request que muta tiene que llevar este header; el frontend lo manda
// incondicionalmente. Esto funciona porque una página de otro origen no puede hacer que el
// navegador adjunte un header custom sin un preflight CORS exitoso, y la única política CORS
// de la API (AddQepCors) sólo deja pasar los orígenes exactos de Cors:AllowedOrigins — la SPA.
// Para cualquier otro origen, incluidas las demás apps bajo *.qcode.co, que son el mismo
// sitio y por eso sí mandan la cookie SameSite=Lax, el preflight falla y el navegador no manda
// el request real. Un comodín, un origen reflejado o SetIsOriginAllowed en esa política
// desactivarían esta defensa en silencio: CorsSettingsValidator lo impide al arrancar, y
// cualquier cambio a la política se revisa junto con esto.
//
// Este header es independiente de los tokens de antiforgery de ASP.NET, y las únicas
// excepciones son los métodos seguros. Un endpoint con DisableAntiforgery —la importación de
// clientes lo necesita para el binding de IFormFile— sigue exigiendo el header: apagar uno no
// apaga el otro. Ese endpoint recibe un multipart POST, que es un request simple y no pasa por
// preflight, así que una app hermana bajo *.qcode.co podría mandarlo con la cookie SameSite=Lax
// del usuario; lo único que lo frena es que no puede agregar X-Qep-Client sin un preflight
// exitoso. Si algún día un endpoint lo llama alguien que no puede mandar el header (un webhook,
// por ejemplo), la excepción tiene que ser explícita para ese endpoint, nunca atada a
// DisableAntiforgery, y ese endpoint no puede aceptar la cookie de sesión: que se autentique con
// su propio mecanismo, como una firma.
internal sealed class RequireCsrfHeaderMiddleware(
    RequestDelegate next,
    IProblemDetailsService problemDetailsService)
{
    private const string HeaderName = "X-Qep-Client";
    private const string ExpectedValue = "web";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        // Spec 2026-10-09 §6.7: la excepción explícita por ruta que este comentario pedía (WebhookPaths,
        // ordinal y con la barra). El webhook no acepta la cookie de sesión; se autentica con la firma
        // HMAC de Meta.
        if (SafeMethods.Contains(context.Request.Method)
            || WebhookPaths.IsWebhook(context.Request.Path)
            || string.Equals(context.Request.Headers[HeaderName], ExpectedValue, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Missing required client header.",
            Detail = $"Non-safe requests must send the '{HeaderName}: {ExpectedValue}' header.",
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
