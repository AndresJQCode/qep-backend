using Microsoft.Extensions.Hosting;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;

namespace Api;

// Shared by every endpoint that issues a session cookie (login, tenant registration)
// so the cookie flags never drift between them.
internal static class SessionCookieWriter
{
    public static void Append(
        HttpContext httpContext,
        QepSessionOptions sessionOptions,
        IHostEnvironment environment,
        SessionIssueResult issued)
    {
        // "Local" runs the real (non-stub) auth flow over plain http on purpose, so a
        // developer can exercise it without a local TLS cert (see Program.cs). Every
        // other environment must set Secure — the cookie is worthless without it.
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
