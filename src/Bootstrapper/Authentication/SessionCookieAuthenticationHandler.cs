using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;

namespace Bootstrapper.Authentication;

// Default authentication scheme for the real (non-dev-stub) provider. Reads the
// opaque session cookie, looks it up against identity.sessions (ISessionService),
// and — unlike a self-contained encrypted cookie — can be invalidated instantly from
// the server side (see docs/decisions on session-cookie strategy). Deliberately the
// ONLY scheme most endpoints ever see: the Google bearer scheme is pinned exclusively
// to POST /auth/session (see QepServiceCollectionExtensions.AddAuthentication) so a
// still-valid Google id token can never be used to bypass a revoked session.
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
        // No cookie: this is the common case for anonymous/health-check traffic.
        // Return immediately without touching the database.
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
