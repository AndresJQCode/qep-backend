using System.Text.Json;
using Modules.Identity.Application;
using Modules.Tenancy.Application;

namespace Api;

/// <summary>
/// Registro y login con email y contraseña. Todos son anónimos: pasan antes de que exista una
/// sesión y no revelan si un email tiene cuenta.
/// </summary>
public static class PasswordAuthEndpoints
{
    public static IEndpointRouteBuilder MapPasswordAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/auth/password/register-tenant", RegisterTenantAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .Accepts<RegisterTenantWithPasswordRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
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

        RegisterTenantWithPasswordRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<RegisterTenantWithPasswordRequest>(
                cancellationToken);
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
