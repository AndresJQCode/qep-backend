using System.Security.Claims;
using Bootstrapper.Authentication;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;
using Modules.Tenancy.Application;

namespace Api;

/// <summary>
/// Auto-registro público de tenants (ADR 0017). La disponibilidad la gobierna el flag
/// <c>Registration:PublicTenantSignupEnabled</c> (false por defecto); el backend es la
/// única fuente de verdad y siempre lo hace cumplir. Registrar un tenant es la única
/// excepción al aprovisionamiento sólo-por-invitación y está acotado al owner del tenant
/// nuevo.
/// </summary>
public static class RegistrationEndpoints
{
    private const string Provider = "google";
    private const string FlagKey = "Registration:PublicTenantSignupEnabled";

    public static IEndpointRouteBuilder MapRegistrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/auth/registration-policy", GetPolicy)
            .AllowAnonymous()
            .WithTags("Authentication")
            .Produces<RegistrationPolicyResponse>();

        // El registro pasa antes de que exista cualquier sesión, directo del bearer token
        // de Google (mismo razonamiento que /auth/session — ver AuthSessionEndpoints
        // y QepServiceCollectionExtensions.AddAuthentication).
        var registerTenant = endpoints.MapPost("/api/v1/auth/register-tenant", RegisterAsync);
        registerTenant.RequireGoogleBearerOrDevStub(endpoints.ServiceProvider);
        registerTenant
            .WithTags("Authentication")
            .Produces<RegisterTenantResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static IResult GetPolicy(IConfiguration configuration) =>
        Results.Ok(new RegistrationPolicyResponse(IsEnabled(configuration)));

    private static async Task<IResult> RegisterAsync(
        HttpContext httpContext,
        RegisterTenantRequest request,
        IConfiguration configuration,
        IOwnerProvisioning ownerProvisioning,
        ITenantRegistration tenantRegistration,
        ISessionService sessionService,
        IOptions<QepSessionOptions> sessionOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(configuration))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Public tenant registration is disabled.");
        }

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
        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "A verified email is required to register a tenant.",
                detail: "email_not_verified");
        }

        var ownerUserId = await ownerProvisioning.ProvisionOwnerAsync(
            Provider,
            subject,
            email,
            cancellationToken);

        var tenantId = await tenantRegistration.RegisterOwnerTenantAsync(
            ownerUserId,
            new TenantRegistrationData(
                request.DisplayName,
                request.Slug,
                request.DefaultCulture,
                request.TimeZone,
                request.DateFormat),
            httpContext.TraceIdentifier,
            cancellationToken);

        // El owner nuevo queda autenticado de acá en adelante — el frontend navega
        // directo a la página de configuración del tenant después de registrar, y eso
        // necesita una sesión viva (ver AuthSessionEndpoints.EstablishAsync, el mismo paso).
        var issued = await sessionService.IssueAsync(
            ownerUserId,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        SessionCookieWriter.Append(httpContext, sessionOptions.Value, environment, issued);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/settings",
            new RegisterTenantResponse(tenantId, ownerUserId));
    }

    private static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>(FlagKey);
}

public sealed record RegistrationPolicyResponse(bool PublicTenantSignupEnabled);

public sealed record RegisterTenantRequest(
    string DisplayName,
    string Slug,
    string DefaultCulture,
    string TimeZone,
    string DateFormat);

public sealed record RegisterTenantResponse(Guid TenantId, Guid OwnerUserId);
