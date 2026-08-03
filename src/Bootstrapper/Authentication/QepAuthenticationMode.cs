using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.Authentication;

public static class QepAuthenticationMode
{
    // Auth mode is decoupled from the hosting environment: the real provider is the
    // default. The header stub is opt-in via Authentication:UseDevelopmentStub, whose
    // default is on only in the Development environment so integration tests keep
    // frictionless header auth. See docs/decisions/0001-development-auth-stub.md.
    public static bool UseDevelopmentStub(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue("Authentication:UseDevelopmentStub", environment.IsDevelopment());

    /// <summary>
    /// Pins an endpoint to the Google bearer token — used only by the handful of
    /// pre-session endpoints (establishing a session, registering a new tenant) that
    /// read <c>sub</c>/<c>email</c>/<c>email_verified</c> straight off the provider
    /// token before any QEP session cookie exists. The dev-stub never registers a
    /// "GoogleBearer" scheme (its single "Development" scheme already simulates these
    /// claims via X-Email/X-Email-Verified), so this only applies to the real branch;
    /// in dev-stub mode it falls back to the default (dev-stub) scheme.
    /// </summary>
    public static void RequireGoogleBearerOrDevStub(
        this RouteHandlerBuilder endpoint,
        IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (UseDevelopmentStub(configuration, environment))
        {
            endpoint.RequireAuthorization();
        }
        else
        {
            endpoint.RequireAuthorization(policy => policy
                .AddAuthenticationSchemes("GoogleBearer")
                .RequireAuthenticatedUser());
        }
    }
}
