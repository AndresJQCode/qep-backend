using System.Security.Claims;
using Bootstrapper.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;
using Modules.Tenancy.Application;

namespace Api;

/// <summary>
/// Endpoints de la raíz de composición que establecen, revalidan y terminan una sesión QEP.
/// La API es un resource server: la SPA se autentica con el proveedor OIDC vía
/// Authorization Code + PKCE y llama a <c>POST /auth/session</c> con el bearer token
/// del proveedor — el único endpoint de toda la API donde ese esquema se acepta
/// (ver QepServiceCollectionExtensions.AddAuthentication). Lee los claims validados
/// <c>sub</c>/<c>email</c>/<c>email_verified</c>, orquesta los dos contratos de módulo
/// —vincular/activar en Identity, después aceptar la membresía en Tenancy— aplicando
/// las reglas de sólo-por-invitación del ADR 0015, y emite la cookie de sesión de token
/// opaco contra la que autentica todo el resto de los endpoints.
/// </summary>
public static class AuthSessionEndpoints
{
    // Primer (y único) proveedor externo, según el ADR 0014.
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
        SessionEstablisher establisher,
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
            // Sólo por invitación: las identidades desconocidas o no verificadas se rechazan sin
            // filtrar si el email existe.
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: outcome.DenialReason);
        }

        return await establisher.EstablishAsync(httpContext, outcome.UserId!.Value, email, cancellationToken);
    }

    private static async Task<IResult> GetCurrentSessionAsync(
        HttpContext httpContext,
        SessionEstablisher establisher,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        var email = await userDirectory.GetEmailAsync(userId.Value, cancellationToken);
        return Results.Ok(await establisher.DescribeAsync(userId.Value, email, cancellationToken));
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

        // El Path tiene que coincidir con el que usó AppendSessionCookie, o el navegador trata
        // esto como otra cookie y nunca borra la real.
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

/// <summary>
/// <c>ActiveTenants</c> lleva el nombre de cada tenant activo porque el selector de tenant del
/// menú de usuario no puede pedirle a la persona que elija entre GUIDs — con sólo el id
/// tendría que adivinar cuál es cuál. <c>ActiveTenantIds</c> se mantiene sin cambios, por
/// compatibilidad con quien todavía sólo lee ids. Cada tenant lleva además los roles de la
/// persona en él, con su nombre, para que el menú de usuario muestre «Administrador» y no la
/// clave.
/// </summary>
/// <remarks><c>Currencies</c> is the global catalogue (spec D2): the SPA keeps no currency list
/// and needs symbols and decimals before the first paint, for every member — including roles
/// without <c>tenancy.settings.read</c>.</remarks>
public sealed record SessionResponse(
    Guid UserId,
    string? Email,
    IReadOnlyCollection<Guid> ActiveTenantIds,
    IReadOnlyCollection<ActiveTenantResponse> ActiveTenants,
    IReadOnlyList<CurrencyInfo> Currencies);

/// <summary>
/// Un tenant activo con los roles de la persona en él. Record propio de la API y no el
/// <see cref="ActiveTenantSummary"/> de Tenancy: el nombre del rol sale del catálogo de
/// Authorization, que Tenancy no puede consultar.
/// </summary>
/// <remarks>
/// El formato regional viaja acá y no sólo en <c>GET /settings</c>: ese endpoint exige
/// <c>tenancy.settings.read</c>, que roles como <c>cashier</c> no tienen, y la SPA necesita el
/// formato antes de la primera pintura. No es sensible: es cómo se muestran fechas y montos de un
/// tenant al que la persona pertenece. Null sólo si el tenant no tiene fila.
/// <c>CurrenciesInUse</c>: currencies with at least one product price in the tenant, catalogue
/// order (spec D10). <c>Country</c> and <c>PersonTypeRequirement</c> (spec 2026-10-26): the
/// requirement is the **effective** one (<c>Never</c> outside Colombia), so the SPA does not repeat
/// the country rule.
/// </remarks>
public sealed record ActiveTenantResponse(
    Guid TenantId,
    string DisplayName,
    IReadOnlyCollection<SessionRoleResponse> Roles,
    string? TimeZone,
    string? DateFormat,
    string? DefaultCurrency,
    string? NumberFormat,
    IReadOnlyList<string> CurrenciesInUse,
    string? Country,
    string? PersonTypeRequirement);

/// <summary>
/// <c>Role</c> es la clave que guarda la membresía; <c>DisplayName</c>, el nombre que ve la
/// persona. El cliente muestra el nombre tal cual y no traduce la clave
/// (QepServiceCollectionExtensions.cs:509-513).
/// </summary>
public sealed record SessionRoleResponse(string Role, string DisplayName);
