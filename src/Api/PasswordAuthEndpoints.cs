using System.Text.Json;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

/// <summary>
/// Registro y login con email y contraseña. Todos son anónimos: pasan antes de que exista una
/// sesión y no revelan si un email tiene cuenta.
/// </summary>
public static partial class PasswordAuthEndpoints
{
    public static IEndpointRouteBuilder MapPasswordAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // .Accepts<T>() hace que el router conteste 415 a un Content-Type que no es JSON (nunca un 500);
        // el 400 queda para un cuerpo JSON mal formado o ausente, que decide el handler.
        endpoints.MapPost("/api/v1/auth/password/register-tenant", RegisterTenantAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Authentication")
            .Accepts<RegisterTenantWithPasswordRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost("/api/v1/auth/email/verify", VerifyEmailAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Authentication")
            .Accepts<VerifyEmailRequest>("application/json")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost("/api/v1/auth/password/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Authentication")
            .Accepts<PasswordLoginRequest>("application/json")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost("/api/v1/auth/password/forgot", ForgotPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Authentication")
            .Accepts<ForgotPasswordRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted);

        endpoints.MapPost("/api/v1/auth/password/reset", ResetPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Credentials)
            .WithTags("Authentication")
            .Accepts<ResetPasswordRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    // 202 siempre que el cuerpo sea JSON: exista el email o no, la respuesta es la misma.
    private static async Task<IResult> ForgotPasswordAsync(
        HttpContext httpContext,
        IPasswordReset passwordReset,
        CancellationToken cancellationToken)
    {
        ForgotPasswordRequest? request = null;
        try
        {
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<ForgotPasswordRequest>(cancellationToken);
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

        await passwordReset.RequestAsync(
            request.Email ?? string.Empty,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.Accepted(uri: null, value: new { });
    }

    // No abre sesión: revoca todas las del usuario y el login con la contraseña nueva la abre. El
    // token y la contraseña no se registran en ningún lado.
    private static async Task<IResult> ResetPasswordAsync(
        HttpContext httpContext,
        IPasswordReset passwordReset,
        CancellationToken cancellationToken)
    {
        ResetPasswordRequest? request = null;
        try
        {
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<ResetPasswordRequest>(cancellationToken);
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

        await passwordReset.ResetAsync(
            request.Token ?? string.Empty,
            request.Password ?? string.Empty,
            cancellationToken);
        return Results.NoContent();
    }

    // Un email desconocido, un usuario sin credencial y una contraseña errónea salen como el mismo
    // 401 identity.credentials.invalid. Es un 401 de credenciales, no de sesión vencida: el frontend
    // lo distingue por el código. Ni la contraseña ni el hash se registran en ningún lado.
    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        IPasswordLogin passwordLogin,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        PasswordLoginRequest? request = null;
        try
        {
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<PasswordLoginRequest>(cancellationToken);
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

        // Un JSON con null en estos campos llega al validador como vacío (422), nunca como un 500.
        var user = await passwordLogin.LoginAsync(
            request.Email ?? string.Empty,
            request.Password ?? string.Empty,
            httpContext.TraceIdentifier,
            cancellationToken);
        return await establisher.EstablishAsync(httpContext, user.UserId, user.Email, cancellationToken);
    }

    // Un token inválido, vencido o usado sale como el mismo 422 identity.email_token.invalid. El
    // token crudo no se registra en ningún lado.
    private static async Task<IResult> VerifyEmailAsync(
        HttpContext httpContext,
        IEmailVerification emailVerification,
        SessionEstablisher establisher,
        CancellationToken cancellationToken)
    {
        VerifyEmailRequest? request = null;
        try
        {
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<VerifyEmailRequest>(cancellationToken);
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

        // Un JSON con token null es un token que no existe: el mismo 422 que cualquier otro.
        var user = await emailVerification.VerifyAsync(request.Token ?? string.Empty, cancellationToken);
        return await establisher.EstablishAsync(httpContext, user.UserId, user.Email, cancellationToken);
    }

    // Orden fijo, y cada paso depende del anterior para no delatar si el email existe:
    // flag -> cuerpo -> validar -> formato y slug libre (antes de cualquier rama por usuario, así un
    // 422 por slug tomado es idéntico con email nuevo o existente) -> aprovisionar -> tenant -> mail.
    // El mail de verificación sale después del tenant: quien pierde la carrera por el slug no recibe
    // ninguno. La respuesta es siempre un 202 vacío.
    private static async Task<IResult> RegisterTenantAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        IPasswordRegistration passwordRegistration,
        ITenantRegistration tenantRegistration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!RegistrationEndpoints.IsEnabled(configuration))
        {
            // Antes de leer el cuerpo: con el signup apagado no se parsea nada del cliente.
            return RegistrationEndpoints.SignupDisabledProblem();
        }

        RegisterTenantWithPasswordRequest? request = null;
        try
        {
            // Sin Content-Type JSON, ReadFromJsonAsync tira InvalidOperationException (un 500).
            if (httpContext.Request.HasJsonContentType())
            {
                request = await httpContext.Request.ReadFromJsonAsync<RegisterTenantWithPasswordRequest>(
                    cancellationToken);
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

        await passwordRegistration.ValidateAsync(request.Email, request.Password, cancellationToken);

        // Un JSON con null en estos campos llegaría al dominio como NullReferenceException (500).
        if (request.Slug is null)
        {
            throw new TenantDomainException("tenancy.slug.invalid", "Tenant slug is required.");
        }

        if (request.DisplayName is null)
        {
            throw new TenantDomainException(
                "tenancy.settings.display_name.invalid",
                "Display name is required.");
        }

        await tenantRegistration.EnsureRegistrableAsync(
            RegistrationEndpoints.ToRegistrationData(
                request.DisplayName,
                request.Slug,
                request.DefaultCulture,
                request.TimeZone,
                request.DateFormat),
            cancellationToken);

        var correlationId = httpContext.TraceIdentifier;
        var outcome = await passwordRegistration.ProvisionOwnerAsync(
            request.Email,
            request.Password,
            correlationId,
            cancellationToken);
        if (outcome.IsExistingAccount)
        {
            return Results.Accepted(uri: null, value: new { });
        }

        var ownerUserId = outcome.CreatedUserId!.Value;
        try
        {
            await tenantRegistration.RegisterOwnerTenantAsync(
                ownerUserId,
                RegistrationEndpoints.ToRegistrationData(
                    request.DisplayName,
                    request.Slug,
                    request.DefaultCulture,
                    request.TimeZone,
                    request.DateFormat),
                correlationId,
                cancellationToken);
        }
        catch (Exception)
        {
            // Cualquier falla del paso del tenant (slug perdido en la carrera, error transitorio de la
            // base, request cancelado): el usuario lo creó este request, así que se borra. Si quedara,
            // ese email tomaría para siempre el camino de «cuenta existente» y nunca tendría tenant.
            // El 422 de slug.taken sale igual que el del chequeo previo; el resto, como siempre.
            try
            {
                await passwordRegistration.DiscardCreatedUserAsync(ownerUserId, CancellationToken.None);
            }
            catch (Exception discardFailure)
            {
                // No enmascara el error original: se registra y se relanza aquel.
                LogDiscardFailed(
                    loggerFactory.CreateLogger(nameof(PasswordAuthEndpoints)),
                    discardFailure,
                    ownerUserId);
            }

            throw;
        }

        await passwordRegistration.RequestEmailVerificationAsync(
            ownerUserId,
            correlationId,
            cancellationToken);

        return Results.Accepted(uri: null, value: new { });
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not discard user {UserId} after the tenant step of register-tenant failed.")]
    private static partial void LogDiscardFailed(ILogger logger, Exception exception, Guid userId);
}

public sealed record RegisterTenantWithPasswordRequest(
    string Email,
    string Password,
    string DisplayName,
    string Slug,
    string? DefaultCulture = null,
    string? TimeZone = null,
    string? DateFormat = null);

public sealed record VerifyEmailRequest(string Token);

public sealed record PasswordLoginRequest(string Email, string Password);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string Password);
