using Microsoft.Extensions.Hosting;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;

namespace Api;

// Compartido por todo endpoint que emite una cookie de sesión (login, registro de tenant)
// para que los flags de la cookie nunca se desincronicen entre ellos.
internal static class SessionCookieWriter
{
    public static void Append(
        HttpContext httpContext,
        QepSessionOptions sessionOptions,
        IHostEnvironment environment,
        SessionIssueResult issued)
    {
        // "Local" corre el flujo de auth real (sin stub) sobre http plano a propósito, para que
        // un developer pueda ejercitarlo sin un certificado TLS local (ver Program.cs). Todos
        // los demás entornos tienen que setear Secure — sin eso la cookie no vale nada.
        var allowInsecureCookie = environment.IsDevelopment() || environment.IsEnvironment("Local");
        httpContext.Response.Cookies.Append(
            sessionOptions.CookieName,
            issued.RawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !allowInsecureCookie,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = issued.ExpiresAt
            });
    }
}
