using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Bootstrapper.Authentication;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;
using Modules.Tenancy.Application;

namespace Api;

/// <summary>
/// Composition-root endpoints that establish, revalidate and end a QEP session.
/// The API is a resource server: the SPA authenticates with the OIDC provider via
/// Authorization Code + PKCE and calls <c>POST /auth/session</c> with the provider
/// bearer token — the only endpoint in the whole API that scheme is ever accepted
/// on (see QepServiceCollectionExtensions.AddAuthentication). It reads the validated
/// <c>sub</c>/<c>email</c>/<c>email_verified</c> claims, orchestrates the two module
/// contracts — Identity link/activate, then Tenancy membership acceptance — applying
/// the ADR 0015 invitation-only rules, and issues the opaque-token session cookie
/// every other endpoint authenticates against.
/// </summary>
public static class AuthSessionEndpoints
{
    // First (and only) external provider, per ADR 0014.
    private const string Provider = "google";

    public static IEndpointRouteBuilder MapAuthSessionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var establishSession = endpoints.MapPost("/api/v1/auth/session", EstablishAsync);
        establishSession.RequireGoogleBearerOrDevStub(endpoints.ServiceProvider);
        establishSession
            .WithTags("Authentication")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet("/api/v1/auth/me", GetCurrentSessionAsync)
            .RequireAuthorization()
            .WithTags("Authentication")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        endpoints.MapPost("/api/v1/auth/logout", LogoutAsync)
            .RequireAuthorization()
            .WithTags("Authentication")
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> EstablishAsync(
        HttpContext httpContext,
        IProviderLinking providerLinking,
        IMembershipActivation membershipActivation,
        ISessionService sessionService,
        IOptions<QepSessionOptions> sessionOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var subject = user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The token is missing a subject claim.");
        }

        var email = user.FindFirstValue("email");
        var emailVerified = string.Equals(
            user.FindFirstValue("email_verified"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var outcome = await providerLinking.LinkAndActivateAsync(
            Provider,
            subject,
            email,
            emailVerified,
            cancellationToken);
        if (outcome.IsDenied)
        {
            // Invitation-only: unknown or unverified identities are refused without
            // leaking whether the email exists.
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: outcome.DenialReason);
        }

        var userId = outcome.UserId!.Value;
        var activeTenants = await membershipActivation.AcceptInvitedMembershipsAsync(
            userId,
            httpContext.TraceIdentifier,
            cancellationToken);

        var issued = await sessionService.IssueAsync(
            userId,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        SessionCookieWriter.Append(httpContext, sessionOptions.Value, environment, issued);

        return Results.Ok(new SessionResponse(userId, email, activeTenants));
    }

    private static async Task<IResult> GetCurrentSessionAsync(
        HttpContext httpContext,
        IActiveTenantsQuery activeTenantsQuery,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        var email = await userDirectory.GetEmailAsync(userId.Value, cancellationToken);
        var activeTenants = await activeTenantsQuery.ListActiveTenantIdsAsync(
            userId.Value,
            cancellationToken);
        return Results.Ok(new SessionResponse(userId.Value, email, activeTenants));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        ISessionService sessionService,
        IOptions<QepSessionOptions> sessionOptions,
        CancellationToken cancellationToken)
    {
        var cookieName = sessionOptions.Value.CookieName;
        if (httpContext.Request.Cookies.TryGetValue(cookieName, out var rawToken)
            && !string.IsNullOrEmpty(rawToken))
        {
            await sessionService.RevokeAsync(rawToken, "logout", cancellationToken);
        }

        // Path must match what AppendSessionCookie used, or the browser treats this
        // as a different cookie and never removes the real one.
        httpContext.Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var value = httpContext.User.FindFirstValue(
            Bootstrapper.Authentication.QepClaimTypes.QepSubject);
        return Guid.TryParse(value, out var userId) ? userId : null;
    }
}

public sealed record SessionResponse(
    Guid UserId,
    string? Email,
    IReadOnlyCollection<Guid> ActiveTenantIds);
