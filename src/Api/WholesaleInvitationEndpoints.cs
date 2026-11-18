using System.Security.Claims;
using System.Text.Json;
using Bootstrapper;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Api;

/// <summary>
/// La invitación de un mayorista vista desde el link (contrato «Invitación», D-4, D-20), fuera de cualquier tenant. Vive en la
/// raíz de composición, con la forma de <see cref="InvitationEndpoints"/>: junta Wholesale (la invitación), Identity (usuario,
/// contraseña y proveedor) y Tenancy (el lock de ciclo de vida del usuario), que el módulo no ve.
/// <para>El token es la llave de todo y nunca sale del request: no hay log ni auditoría que lo nombre, y
/// <c>RequestFailureCapture</c> guarda la ruta con <c>SensitivePaths.Mask</c>. Las rutas anónimas van limitadas por IP:
/// <c>Public</c> para leer y <c>Credentials</c> para las que abren sesión.</para>
/// </summary>
public static class WholesaleInvitationEndpoints
{
    private const string Route = "/api/v1/wholesale-invitations/{token}";

    private static readonly AcceptWholesaleInvitationValidator AcceptValidator = new();

    public static IEndpointRouteBuilder MapWholesaleInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Quien abre el link todavía no tiene sesión: necesita saber quién lo invita y con qué cuenta entrar.
        endpoints.MapGet(Route, GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Public)
            .WithTags("Wholesale invitations")
            .Produces<WholesaleInvitationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // D-4: el login de Google es sólo por invitación; ésta aprovisiona al usuario por el correo invitado y lo vincula.
        var session = endpoints.MapPost($"{Route}/session", SessionAsync);
        session.RequireGoogleBearerOrDevStub(endpoints.ServiceProvider);
        session
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Wholesale invitations")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        // Anónimo, como accept-with-password de miembros: el link prueba el buzón y la sesión sale de esta llamada.
        endpoints.MapPost($"{Route}/session-with-password", SessionWithPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Wholesale invitations")
            .Accepts<WholesaleInvitationPasswordRequest>("application/json")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet($"{Route}/linkable-tenants", ListLinkableTenantsAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiterPolicies.Public)
            .WithTags("Wholesale invitations")
            .Produces<LinkableTenantsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        endpoints.MapPost($"{Route}/accept", AcceptAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiterPolicies.Public)
            .WithTags("Wholesale invitations")
            .Accepts<AcceptWholesaleInvitationRequest>("application/json")
            .Produces<AcceptedInvitationDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    // D-20: el correo completo y si ya tiene contraseña, como GET /invitations/{token}. Un correo sin usuario no tiene.
    private static async Task<IResult> GetAsync(
        string token,
        IDistributorInvitationService invitations,
        IUserDirectory userDirectory,
        IPasswordCredentials passwordCredentials,
        CancellationToken cancellationToken)
    {
        var summary = await invitations.FindAsync(token, cancellationToken);
        var userId = await userDirectory.FindUserIdByEmailAsync(summary.Email, cancellationToken);
        var hasPassword = userId is { } id && await passwordCredentials.HasPasswordAsync(id, cancellationToken);

        return Results.Ok(new WholesaleInvitationResponse(
            summary.WholesalerName,
            summary.BusinessName,
            summary.ContactName,
            summary.Email,
            summary.Status,
            summary.ExpiresAt,
            hasPassword));
    }

    // Orden fijo: invitación aceptable (404 → 410 → 409) → correo del token verificado y el invitado (403) → bajo el lock de
    // ciclo de vida del usuario, vincular y, sólo si la identidad no tiene usuario, aprovisionar (WholesaleInvitationGoogleSession)
    // → el usuario es el del correo invitado (403) → commit → sesión de ese usuario. El correo se mira antes de tocar Identity.
    private static async Task<IResult> SessionAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        WholesaleInvitationGoogleSession googleSession,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        var invitation = await invitations.RequirePendingAsync(token, cancellationToken);

        var principal = httpContext.User;
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The token is missing a subject claim.");
        }

        var email = principal.FindFirstValue("email");
        var emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (!emailVerified || !invitation.InvitedEmailMatches(email))
        {
            throw WholesaleInvitationGoogleSession.EmailMismatch();
        }

        var outcome = await googleSession.OpenAsync(invitation.Email, subject, email!, cancellationToken);
        if (outcome.IsDenied)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: outcome.DenialReason);
        }

        return await establisher.EstablishAsync(httpContext, outcome.UserId!.Value, invitation.Email, cancellationToken);
    }

    // Orden fijo: cuerpo (400) → invitación aceptable → contraseña con las reglas del registro (422, W17) → bajo el lock,
    // aprovisionar por el correo invitado → crear o verificar la contraseña (marca el correo verificado) → commit → sesión. La
    // contraseña no se registra en ningún lado.
    private static async Task<IResult> SessionWithPasswordAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        WholesaleInvitationPasswordSession passwordSession,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        WholesaleInvitationPasswordRequest? request = null;
        try
        {
            // Sin Content-Type JSON, ReadFromJsonAsync tira InvalidOperationException (un 500).
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<WholesaleInvitationPasswordRequest>(cancellationToken);
            }
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request body is missing or malformed.");
        }

        var invitation = await invitations.RequirePendingAsync(token, cancellationToken);
        // Ruling W17: valida la contraseña antes de crear al usuario (WholesaleInvitationPasswordSession).
        var user = await passwordSession.OpenAsync(invitation.Email, request.Password ?? string.Empty, cancellationToken);

        return await establisher.EstablishAsync(httpContext, user.UserId, user.Email, cancellationToken);
    }

    private static async Task<IResult> ListLinkableTenantsAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        if (RequireUserId(httpContext) is not { } userId)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        var email = await userDirectory.GetEmailAsync(userId, cancellationToken);
        var tenants = await invitations.ListLinkableTenantsAsync(token, userId, email, cancellationToken);

        return Results.Ok(new LinkableTenantsResponse(tenants));
    }

    private static async Task<IResult> AcceptAsync(
        string token,
        AcceptWholesaleInvitationRequest request,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        if (RequireUserId(httpContext) is not { } userId)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        await AcceptValidator.ValidateAndThrowAsync(request, cancellationToken);
        AcceptInvitationChoice choice = AcceptWholesaleInvitationValidator.IsNewTenant(request.Mode)
            ? new NewTenantChoice(RegistrationEndpoints.ToRegistrationData(request.DisplayName!, request.Slug!, null, null, null))
            : new LinkTenantChoice(request.TenantId!.Value);

        // El correo sale de Identity, no del cuerpo ni de un claim: la sesión QEP no lo trae.
        var email = await userDirectory.GetEmailAsync(userId, cancellationToken);
        var accepted = await invitations.AcceptAsync(
            token, userId, email, choice, httpContext.TraceIdentifier, cancellationToken);

        return Results.Ok(accepted);
    }

    // Mismo criterio que InvitationEndpoints.RequireUserId: primero qep_sub (la cookie de sesión) y "sub" de respaldo, porque el
    // stub de desarrollo pone ahí el subject QEP. Con un bearer real "sub" no es un Guid y no pasa.
    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var value = httpContext.User.FindFirstValue(QepClaimTypes.QepSubject)
            ?? httpContext.User.FindFirstValue(QepClaimTypes.SubjectId);
        return Guid.TryParse(value, out var userId) ? userId : null;
    }
}

/// <summary>El resumen del link (D-20) más <c>HasPassword</c>, que sólo la composición sabe (Identity).</summary>
public sealed record WholesaleInvitationResponse(
    string WholesalerName,
    string BusinessName,
    string ContactName,
    string Email,
    string Status,
    DateTimeOffset ExpiresAt,
    bool HasPassword);

public sealed record WholesaleInvitationPasswordRequest(string? Password);

/// <summary>Los tenants que quien acepta administra como owner, con por qué no se puede elegir cada uno.</summary>
public sealed record LinkableTenantsResponse(IReadOnlyList<LinkableTenantDto> Tenants);

/// <summary>
/// <c>Mode</c> <c>NewTenant</c> con <c>DisplayName</c> y <c>Slug</c> (las reglas de <c>register-tenant</c>), o <c>LinkTenant</c>
/// con <c>TenantId</c>. Todo opcional en el tipo para que lo que falte sea un 422 con su campo y no un 400 del binding.
/// </summary>
public sealed record AcceptWholesaleInvitationRequest(string? Mode, string? DisplayName, string? Slug, Guid? TenantId);

/// <summary>Claves del 422: <c>Mode</c>, <c>DisplayName</c>, <c>Slug</c> y <c>TenantId</c>. El formato del nombre y del slug lo
/// valida Tenancy con sus propios códigos (<c>tenancy.slug.invalid</c>, …).</summary>
public sealed class AcceptWholesaleInvitationValidator : AbstractValidator<AcceptWholesaleInvitationRequest>
{
    public const string NewTenantMode = "NewTenant";

    public const string LinkTenantMode = "LinkTenant";

    public AcceptWholesaleInvitationValidator()
    {
        RuleFor(request => request.Mode)
            .Must(mode => IsNewTenant(mode) || IsLinkTenant(mode))
            .WithMessage("Mode must be NewTenant or LinkTenant.");

        When(request => IsNewTenant(request.Mode), () =>
        {
            RuleFor(request => request.DisplayName).NotEmpty();
            RuleFor(request => request.Slug).NotEmpty();
        });

        When(request => IsLinkTenant(request.Mode), () =>
            RuleFor(request => request.TenantId).NotNull().NotEqual(Guid.Empty));
    }

    public static bool IsNewTenant(string? mode) => string.Equals(mode, NewTenantMode, StringComparison.OrdinalIgnoreCase);

    public static bool IsLinkTenant(string? mode) => string.Equals(mode, LinkTenantMode, StringComparison.OrdinalIgnoreCase);
}
