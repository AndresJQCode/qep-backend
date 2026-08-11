using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;

namespace Bootstrapper.Authentication;

// Esquema de autenticación por defecto para el proveedor real (sin stub). Lee la
// cookie de sesión opaca, la busca contra identity.sessions (ISessionService) y
// —a diferencia de una cookie cifrada autocontenida— se puede invalidar al instante
// desde el servidor (ver los docs/decisions sobre la cookie de sesión). Es
// deliberadamente el ÚNICO esquema que ve la mayoría de los endpoints: el bearer de
// Google está fijado sólo a POST /auth/session (QepServiceCollectionExtensions.AddAuthentication),
// así que un id token de Google todavía válido nunca puede saltear una sesión revocada.
internal sealed class SessionCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    IOptions<QepSessionOptions> sessionOptions,
    ISessionService sessionService,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "QepSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Sin cookie: éste es el caso común del tráfico anónimo/de health-check.
        // Vuelve de inmediato sin tocar la base.
        if (!Request.Cookies.TryGetValue(sessionOptions.Value.CookieName, out var rawToken)
            || string.IsNullOrEmpty(rawToken))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await sessionService.ValidateAsync(rawToken, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.NoResult();
        }

        var claims = new List<Claim>
        {
            new(QepClaimTypes.QepSubject, principal.UserId.ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthenticationSchemeName);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            AuthenticationSchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
