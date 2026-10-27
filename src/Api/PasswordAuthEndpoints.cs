using System.Text.Json;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

/// <summary>
/// Registro y login con email y contraseña. Todos son anónimos: pasan antes de que exista una
/// sesión y no revelan si un email tiene cuenta.
/// </summary>
public static class PasswordAuthEndpoints
{
    public static IEndpointRouteBuilder MapPasswordAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // .Accepts<T>() hace que el router conteste 415 a un Content-Type que no es JSON (nunca un 500);
        // el 400 queda para un cuerpo JSON mal formado o ausente, que decide el handler.
        endpoints.MapPost("/api/v1/auth/password/register-tenant", RegisterTenantAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .Accepts<RegisterTenantWithPasswordRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost("/api/v1/auth/email/verify", VerifyEmailAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .Accepts<VerifyEmailRequest>("application/json")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
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
        catch (TenantDomainException exception) when (exception.Code == "tenancy.slug.taken")
        {
            // Perdió la carrera por el slug entre el chequeo y el insert. El usuario lo creó este
            // request, así que se borra: si quedara, ese email tomaría para siempre el camino de
            // «cuenta existente» y nunca tendría tenant. El 422 sale igual que el del chequeo previo.
            await passwordRegistration.DiscardCreatedUserAsync(ownerUserId, CancellationToken.None);
            throw;
        }

        await passwordRegistration.RequestEmailVerificationAsync(
            ownerUserId,
            correlationId,
            cancellationToken);

        return Results.Accepted(uri: null, value: new { });
    }
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
