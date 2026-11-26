using System.Security.Claims;
using System.Text.Json;
using Bootstrapper;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

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

    private const string SlugTakenCode = "tenancy.slug.taken";

    /// <summary><c>InvitationSummaryDto.Status</c> de una invitación aceptable (<c>InvitationViewState.Pending</c> en minúscula).</summary>
    private const string PendingStatus = "pending";

    /// <summary>Reintentos después del primer intento cuando el slug sugerido se toma en una carrera (spec 2026-11-19 §3b).</summary>
    private const int SlugRetries = 3;

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

        // Spec 2026-11-19 §4: el código de un solo uso para el correo que escribió el distribuidor. Siempre 202: no dice si el
        // correo tiene cuenta ni si la invitación sigue pendiente (eso ya lo dice el GET); sólo emite con la invitación pendiente.
        endpoints.MapPost($"{Route}/email-code", EmailCodeAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Wholesale invitations")
            .Accepts<WholesaleInvitationEmailCodeRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted);

        // D-4: el login de Google es sólo por invitación; ésta aprovisiona al usuario por el correo verificado y lo vincula.
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

        // Anónimo, como accept-with-password de miembros. El link por sí solo no prueba el buzón (spec 2026-11-19): una cuenta
        // sin contraseña necesita el código de email-code o la prueba que sólo lleva el link del correo (spec 2026-11-24), y
        // los términos. La sesión sale de esta llamada.
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
        ITenantSlugSuggestions slugSuggestions,
        TermsOptions termsOptions,
        CancellationToken cancellationToken)
    {
        var summary = await invitations.FindAsync(token, cancellationToken);
        var userId = await userDirectory.FindUserIdByEmailAsync(summary.Email, cancellationToken);
        var hasPassword = userId is { } id && await passwordCredentials.HasPasswordAsync(id, cancellationToken);
        // Spec 2026-11-19 §3b: el formulario de aceptar llega prellenado con la razón social y este slug, y se pueden corregir.
        var suggestedSlug = await slugSuggestions.SuggestAsync(summary.BusinessName, cancellationToken);

        return Results.Ok(new WholesaleInvitationResponse(
            summary.WholesalerName,
            summary.BusinessName,
            summary.ContactName,
            summary.Email,
            summary.Status,
            summary.ExpiresAt,
            hasPassword,
            suggestedSlug,
            string.IsNullOrWhiteSpace(termsOptions.CurrentVersion) ? null : termsOptions.CurrentVersion.Trim()));
    }

    // Ni la invitación inexistente o ya no pendiente, ni un correo mal formado, cambian la respuesta: el endpoint no sirve para
    // averiguar nada. El correo inválido no llega a Identity.
    private static async Task<IResult> EmailCodeAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        IEmailChallenges emailChallenges,
        CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<WholesaleInvitationEmailCodeRequest>(httpContext, cancellationToken);
        if (string.IsNullOrWhiteSpace(request?.Email))
        {
            return Results.Accepted();
        }

        try
        {
            await invitations.RequirePendingAsync(token, cancellationToken);
            await emailChallenges.IssueAsync(request.Email, cancellationToken);
        }
        catch (Exception exception) when (exception is ResourceNotFoundException
            or ResourceGoneException
            or ResourceConflictException
            or IdentityDomainException)
        {
            // 404/410/409 de la invitación o un correo que no es una dirección: la misma respuesta que el caso normal.
        }

        return Results.Accepted();
    }

    // Orden fijo: invitación aceptable (404 → 410 → 409) → correo del token verificado (403, sin tocar Identity) → bajo el lock
    // de ciclo de vida de ese correo, vincular y, sólo si la identidad no tiene usuario, aprovisionarlo
    // (WholesaleInvitationGoogleSession) → commit → sesión de ese usuario. Spec 2026-11-19: el correo no tiene que ser el
    // invitado; si es otro, reemplaza al invitado al aceptar.
    private static async Task<IResult> SessionAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        WholesaleInvitationGoogleSession googleSession,
        IUserDirectory userDirectory,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        await invitations.RequirePendingAsync(token, cancellationToken);

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
        if (!emailVerified || string.IsNullOrWhiteSpace(email))
        {
            // El mismo motivo que da Identity (ProviderLinkingService) a un correo sin verificar.
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: "email_not_verified");
        }

        var outcome = await googleSession.OpenAsync(email.Trim(), subject, cancellationToken);
        if (outcome.IsDenied)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Login denied.",
                detail: outcome.DenialReason);
        }

        // El correo de la cuenta, no el del token: un subject ya vinculado puede ser de una cuenta con otro correo.
        var userId = outcome.UserId!.Value;
        var accountEmail = await userDirectory.GetEmailAsync(userId, cancellationToken);
        return await establisher.EstablishAsync(httpContext, userId, accountEmail, cancellationToken);
    }

    // Orden fijo: cuerpo (400) → invitación aceptable → si la prueba del link prueba el buzón de ese correo (Wholesale) →
    // contraseña con las reglas del registro (422, W17) → bajo el lock del correo, con contraseña ya creada sólo verificarla;
    // sin ella, prueba de buzón o código del correo (401) y después términos (422), y recién entonces aprovisionar, crear la
    // contraseña y registrar los términos (WholesaleInvitationPasswordSession) → commit → sesión. La contraseña, el código y
    // la prueba no se registran en ningún lado.
    private static async Task<IResult> SessionWithPasswordAsync(
        string token,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        WholesaleInvitationPasswordSession passwordSession,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<WholesaleInvitationPasswordRequest>(httpContext, cancellationToken);
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request body is missing or malformed.");
        }

        var invitation = await invitations.RequirePendingAsync(token, cancellationToken);
        // Sin correo en el cuerpo (un cliente anterior), el invitado. Ruling W17: valida la contraseña antes de crear al
        // usuario (WholesaleInvitationPasswordSession).
        var email = string.IsNullOrWhiteSpace(request.Email) ? invitation.Email : request.Email.Trim();
        var emailProven = DistributorEmailProofs.Proves(invitation, request.EmailProof, email);
        var user = await passwordSession.OpenAsync(
            email,
            request.Password ?? string.Empty,
            request.Code,
            emailProven,
            request.TermsVersion,
            httpContext.TraceIdentifier,
            cancellationToken);

        return await establisher.EstablishAsync(httpContext, user.UserId, user.Email, cancellationToken);
    }

    /// <summary>El cuerpo JSON, o <c>null</c> si falta o está mal formado. Sin Content-Type JSON, <c>ReadFromJsonAsync</c>
    /// tira <see cref="InvalidOperationException"/> (un 500).</summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext httpContext, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return httpContext.Request.HasJsonContentType()
                ? await httpContext.Request.ReadFromJsonAsync<T>(cancellationToken)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    // Spec 2026-11-19 §3b y §7: para NewTenant el formulario llega prellenado (razón social y slug sugerido) y el distribuidor
    // puede corregirlos: lo que traiga el cuerpo se usa; lo que falte sale de la razón social y de lo que sugiere Tenancy.
    // Quien no aceptó la versión vigente de los términos la manda aquí (422 sin ella).
    private static async Task<IResult> AcceptAsync(
        string token,
        AcceptWholesaleInvitationRequest request,
        HttpContext httpContext,
        IDistributorInvitationService invitations,
        IUserDirectory userDirectory,
        ITermsAcceptances termsAcceptances,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        if (RequireUserId(httpContext) is not { } userId)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        await AcceptValidator.ValidateAndThrowAsync(request, cancellationToken);

        // El correo sale de Identity, no del cuerpo ni de un claim: la sesión QEP no lo trae.
        var email = await userDirectory.GetEmailAsync(userId, cancellationToken);
        if (!AcceptWholesaleInvitationValidator.IsNewTenant(request.Mode))
        {
            return Results.Ok(await invitations.AcceptAsync(
                token, userId, email, new LinkTenantChoice(request.TenantId!.Value), httpContext.TraceIdentifier, cancellationToken));
        }

        // FindAsync y no RequirePendingAsync: repetir la aceptación ya hecha (D-16) tiene que seguir devolviendo el resultado.
        // Los términos sólo se piden (y registran) con la invitación pendiente: una ya consumida o vencida responde su propio
        // estado (409 para otra cuenta, el mismo resultado para quien la aceptó, 410), no un 422 de términos.
        var summary = await invitations.FindAsync(token, cancellationToken);
        if (string.Equals(summary.Status, PendingStatus, StringComparison.Ordinal))
        {
            await EnsureTermsAsync(userId, request.TermsVersion, termsAcceptances, cancellationToken);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? summary.BusinessName : request.DisplayName.Trim();
        var slug = string.IsNullOrWhiteSpace(request.Slug) ? null : request.Slug.Trim();

        return Results.Ok(await AcceptNewTenantAsync(
            token, userId, email, displayName, slug, httpContext, scopeFactory, cancellationToken));
    }

    /// <summary>
    /// La versión del cuerpo se registra (idempotente; el usuario ya está guardado porque tiene sesión). Sin ella, el usuario
    /// tiene que haber aceptado la vigente; si no, 422 en <c>TermsVersion</c>. Sin versión configurada falla cerrado
    /// (<c>identity.terms.unavailable</c>).
    /// </summary>
    private static async Task EnsureTermsAsync(
        Guid userId, string? termsVersion, ITermsAcceptances termsAcceptances, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(termsVersion))
        {
            await termsAcceptances.RecordAsync(userId, termsVersion, cancellationToken);
            return;
        }

        if (!await termsAcceptances.HasAcceptedCurrentAsync(userId, cancellationToken))
        {
            throw new ValidationException(
                [new ValidationFailure("TermsVersion", "The terms version is required.") { ErrorCode = "NotEmptyValidator" }]);
        }
    }

    /// <summary>
    /// Foco 3: si el slug es el sugerido y otra aceptación lo toma entre la sugerencia y el alta (<c>tenancy.slug.taken</c>), se
    /// reintenta con una sugerencia nueva hasta <see cref="SlugRetries"/> veces. Un slug que escribió el distribuidor no se
    /// reemplaza solo: si está tomado sale el 422 para que lo corrija. Cada reintento va en un scope nuevo: si el choque fue en el
    /// guardado, el DbContext de Tenancy del intento anterior se quedó con el tenant reservado sin guardar, y volver a agregarlo
    /// con el mismo id fallaría. La aceptación es idempotente (D-3), así que repetirla entera no duplica nada.
    /// </summary>
    private static async Task<AcceptedInvitationDto> AcceptNewTenantAsync(
        string token,
        Guid userId,
        string? email,
        string displayName,
        string? requestedSlug,
        HttpContext httpContext,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            AsyncServiceScope? fresh = retry == 0 ? null : scopeFactory.CreateAsyncScope();
            try
            {
                var services = fresh?.ServiceProvider ?? httpContext.RequestServices;
                var slug = requestedSlug
                    ?? await services.GetRequiredService<ITenantSlugSuggestions>().SuggestAsync(displayName, cancellationToken);
                var choice = new NewTenantChoice(RegistrationEndpoints.ToRegistrationData(displayName, slug, null, null, null));
                return await services.GetRequiredService<IDistributorInvitationService>().AcceptAsync(
                    token, userId, email, choice, httpContext.TraceIdentifier, cancellationToken);
            }
            catch (TenantDomainException exception) when (exception.Code == SlugTakenCode && requestedSlug is null && retry < SlugRetries)
            {
                // Otra sugerencia en la siguiente vuelta.
            }
            finally
            {
                if (fresh is { } scope)
                {
                    await scope.DisposeAsync();
                }
            }
        }
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

/// <summary>El resumen del link (D-20) más <c>HasPassword</c>, que sólo la composición sabe (Identity), y <c>SuggestedSlug</c>
/// (spec 2026-11-19 §3b), el identificador libre que se le propone al distribuidor para su tenant nuevo, y <c>TermsVersion</c>,
/// la versión vigente de los términos (<c>Terms:CurrentVersion</c>) que la pantalla devuelve al crear la cuenta o el tenant;
/// nula si no hay una configurada.</summary>
public sealed record WholesaleInvitationResponse(
    string WholesalerName,
    string BusinessName,
    string ContactName,
    string Email,
    string Status,
    DateTimeOffset ExpiresAt,
    bool HasPassword,
    string SuggestedSlug,
    string? TermsVersion);

/// <summary>
/// Spec 2026-11-19 §4 y spec 2026-11-24. <c>Email</c> es el que escribió el distribuidor (sin él, el invitado). <c>Code</c>,
/// <c>EmailProof</c> y <c>TermsVersion</c> sólo se miran si esa cuenta todavía no tiene contraseña; entonces los términos son
/// obligatorios y hace falta el código o la prueba. <c>EmailProof</c> es el <c>ev</c> del link del correo: reemplaza al código
/// sólo con el correo invitado y mientras siga vigente; si no, se ignora.
/// </summary>
public sealed record WholesaleInvitationPasswordRequest(
    string? Email, string? Password, string? Code, string? TermsVersion, string? EmailProof = null);

/// <summary>El correo al que se manda el código de un solo uso.</summary>
public sealed record WholesaleInvitationEmailCodeRequest(string? Email);

/// <summary>Los tenants que quien acepta administra como owner o admin, con por qué no se puede elegir cada uno.</summary>
public sealed record LinkableTenantsResponse(IReadOnlyList<LinkableTenantDto> Tenants);

/// <summary>
/// <c>Mode</c> <c>NewTenant</c> (spec 2026-11-19 §3b: <c>DisplayName</c> y <c>Slug</c> opcionales, los del formulario prellenado
/// y corregido; sin ellos, la razón social y el slug sugerido) o <c>LinkTenant</c> con <c>TenantId</c>. <c>TermsVersion</c> la manda quien todavía no aceptó la vigente,
/// y sólo se exige para <c>NewTenant</c>. Todo opcional en el tipo para que lo que falte sea un 422 con su campo y no un 400 del
/// binding.
/// </summary>
public sealed record AcceptWholesaleInvitationRequest(
    string? Mode, Guid? TenantId, string? TermsVersion, string? DisplayName = null, string? Slug = null);

/// <summary>Claves del 422: <c>Mode</c> y <c>TenantId</c>. <c>TermsVersion</c> la exige el endpoint, que es quien sabe si el
/// usuario ya aceptó.</summary>
public sealed class AcceptWholesaleInvitationValidator : AbstractValidator<AcceptWholesaleInvitationRequest>
{
    public const string NewTenantMode = "NewTenant";

    public const string LinkTenantMode = "LinkTenant";

    public AcceptWholesaleInvitationValidator()
    {
        RuleFor(request => request.Mode)
            .Must(mode => IsNewTenant(mode) || IsLinkTenant(mode))
            .WithMessage("Mode must be NewTenant or LinkTenant.");

        When(request => IsLinkTenant(request.Mode), () =>
            RuleFor(request => request.TenantId).NotNull().NotEqual(Guid.Empty));
    }

    public static bool IsNewTenant(string? mode) => string.Equals(mode, NewTenantMode, StringComparison.OrdinalIgnoreCase);

    public static bool IsLinkTenant(string? mode) => string.Equals(mode, LinkTenantMode, StringComparison.OrdinalIgnoreCase);
}
