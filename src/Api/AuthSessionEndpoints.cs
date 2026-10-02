using System.Security.Claims;
using Bootstrapper.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Authorization.Application;
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
        IMembershipActivation membershipActivation,
        IActiveTenantsQuery activeTenantsQuery,
        ITenantRoleCatalog roleCatalog,
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
            // Sólo por invitación: las identidades desconocidas o no verificadas se rechazan sin
            // filtrar si el email existe.
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: outcome.DenialReason);
        }

        var userId = outcome.UserId!.Value;
        // El valor de retorno se descarta a propósito: activeTenantsQuery.ListActiveTenantsAsync
        // de abajo es la única consulta detrás de los dos campos de SessionResponse, para que
        // ids y nombres no puedan discrepar si una membresía cambia entre dos consultas
        // separadas.
        await membershipActivation.AcceptInvitedMembershipsAsync(
            userId,
            httpContext.TraceIdentifier,
            cancellationToken);
        var activeTenants = await activeTenantsQuery.ListActiveTenantsAsync(
            userId,
            cancellationToken);
        var activeTenantIds = activeTenants.Select(tenant => tenant.TenantId).ToArray();
        // Antes de emitir la sesión: si leer el catálogo falla, que no quede una cookie viva
        // detrás de una respuesta de error.
        var tenants = await ToActiveTenantResponsesAsync(
            activeTenants,
            roleCatalog,
            cancellationToken);

        var issued = await sessionService.IssueAsync(
            userId,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        SessionCookieWriter.Append(httpContext, sessionOptions.Value, environment, issued);

        return Results.Ok(new SessionResponse(userId, email, activeTenantIds, tenants));
    }

    private static async Task<IResult> GetCurrentSessionAsync(
        HttpContext httpContext,
        IActiveTenantsQuery activeTenantsQuery,
        ITenantRoleCatalog roleCatalog,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        var email = await userDirectory.GetEmailAsync(userId.Value, cancellationToken);
        var activeTenants = await activeTenantsQuery.ListActiveTenantsAsync(
            userId.Value,
            cancellationToken);
        var activeTenantIds = activeTenants.Select(tenant => tenant.TenantId).ToArray();
        var tenants = await ToActiveTenantResponsesAsync(
            activeTenants,
            roleCatalog,
            cancellationToken);
        return Results.Ok(new SessionResponse(userId.Value, email, activeTenantIds, tenants));
    }

    /// <summary>
    /// Le pone a cada rol de cada tenant activo el nombre que ve la persona.
    /// </summary>
    /// <remarks>
    /// Se resuelve acá y no en Tenancy porque el catálogo de roles es de Authorization, que ya
    /// referencia a Tenancy: consultarlo desde Tenancy cerraría un ciclo. El catálogo es por
    /// tenant —los de sistema más los custom de ese tenant (TenantRoleCatalog.cs:62-110)—, así
    /// que se pide uno por tenant.
    ///
    /// Una clave que el catálogo no conoce sale con la clave como nombre y no se descarta:
    /// puede ser un rol retirado que la membresía todavía nombra, el mismo caso que
    /// TenantRoleCatalog.cs:32-34 tolera sin fallar. Descartarla escondería un rol que la
    /// membresía sí tiene. El orden es el de la membresía, no el del catálogo.
    /// </remarks>
    private static async Task<IReadOnlyCollection<ActiveTenantResponse>> ToActiveTenantResponsesAsync(
        IReadOnlyCollection<ActiveTenantSummary> activeTenants,
        ITenantRoleCatalog roleCatalog,
        CancellationToken cancellationToken)
    {
        var responses = new List<ActiveTenantResponse>(activeTenants.Count);
        foreach (var tenant in activeTenants)
        {
            var catalog = await roleCatalog.ListRolesAsync(tenant.TenantId, cancellationToken);
            var displayNames = catalog.ToDictionary(
                definition => definition.Role,
                definition => definition.DisplayName,
                StringComparer.Ordinal);

            responses.Add(new ActiveTenantResponse(
                tenant.TenantId,
                tenant.DisplayName,
                tenant.Roles
                    .Select(role => new SessionRoleResponse(
                        role,
                        displayNames.GetValueOrDefault(role, role)))
                    .ToArray()));
        }

        return responses;
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
public sealed record SessionResponse(
    Guid UserId,
    string? Email,
    IReadOnlyCollection<Guid> ActiveTenantIds,
    IReadOnlyCollection<ActiveTenantResponse> ActiveTenants);

/// <summary>
/// Un tenant activo con los roles de la persona en él. Record propio de la API y no el
/// <see cref="ActiveTenantSummary"/> de Tenancy: el nombre del rol sale del catálogo de
/// Authorization, que Tenancy no puede consultar.
/// </summary>
public sealed record ActiveTenantResponse(
    Guid TenantId,
    string DisplayName,
    IReadOnlyCollection<SessionRoleResponse> Roles);

/// <summary>
/// <c>Role</c> es la clave que guarda la membresía; <c>DisplayName</c>, el nombre que ve la
/// persona. El cliente muestra el nombre tal cual y no traduce la clave
/// (QepServiceCollectionExtensions.cs:509-513).
/// </summary>
public sealed record SessionRoleResponse(string Role, string DisplayName);
