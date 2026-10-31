using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// El esquema <c>ApiKey</c> (spec 2026-10-30 §4.3): toma <c>X-Api-Key</c>, valida contra la base en cada petición y arma un principal
/// <b>sin claims <c>permission</c></b>, para que ninguna política interna pase con una clave aunque alguien cambie un esquema por error.
/// <c>qep_sub</c> y <c>tenant_id</c> van en esta misma identidad, que es lo que hace que <c>ExternalClaimsTransformation</c> la deje
/// quieta. Cualquier rechazo es el mismo 401; la causa real solo va al log, con el prefijo y nunca el valor presentado.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IApiKeyVerifier verifier,
    IApiKeyRepository keys,
    IClock clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Una cabecera repetida no es una clave: se trata como malformada en vez de elegir una de las dos.
        var header = Request.Headers[ApiKeyDefaults.HeaderName];
        string? presented = header.Count switch
        {
            0 => null,
            1 => header[0],
            _ => string.Empty,
        };

        var verification = await verifier.VerifyAsync(presented, Context.RequestAborted);
        if (verification.Key is not { } key)
        {
            // Unknown y Malformed son ruido de quien prueba claves inventadas (el tope por IP aun deja 600 por minuto): Debug.
            // Revoked, Expired y SecretMismatch nombran una clave real y vale verlos.
            if (verification.Rejection is ApiKeyRejection.Unknown or ApiKeyRejection.Malformed)
            {
                LogRejectedNoise(Logger, verification.Rejection, verification.KeyPrefix);
            }
            else
            {
                LogRejected(Logger, verification.Rejection, verification.KeyPrefix);
            }

            return AuthenticateResult.Fail("Invalid API key.");
        }

        // Un contador no puede tumbar un envío (spec §6.2): si falla se loguea y se sigue.
        try
        {
            await keys.TouchLastUsedAsync(new ApiKeyId(key.Id), clock.UtcNow, Context.RequestAborted);
        }
#pragma warning disable CA1031 // Cualquier falla del contador se absorbe a propósito.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogTouchFailed(Logger, exception, key.KeyPrefix);
        }

        List<Claim> claims =
        [
            new(QepClaimTypes.QepSubject, key.Id.ToString()),
            new(QepClaimTypes.TenantId, key.TenantId.ToString()),
            new(ApiKeyClaimTypes.KeyId, key.Id.ToString()),
            new(ApiKeyClaimTypes.KeyName, key.Name),
        ];
        claims.AddRange(key.Scopes.Select(scope => new Claim(ApiKeyClaimTypes.Scope, scope)));
        if (key.ConnectionIds is not null)
        {
            claims.AddRange(key.ConnectionIds.Select(id => new Claim(ApiKeyClaimTypes.Connection, id.ToString())));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.AuthenticationScheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status401Unauthorized, "Unauthorized", ExternalApiErrorCodes.InvalidKey, "La clave de API no viene en la petición o no es válida.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status403Forbidden, "Access denied", ExternalApiErrorCodes.ScopeMissing, "Tu clave de API no tiene el permiso necesario para esta operación.");

    // El comportamiento por defecto es un cuerpo vacío, y un integrador externo no tiene otra forma de saber qué pasó.
    private async Task WriteProblemAsync(int status, string title, string code, string detail)
    {
        Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? Context.TraceIdentifier;

        // Se resuelve al escribir y no en el constructor: el contenedor de la composición se valida sin AddProblemDetails.
        var written = await Context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
        if (!written)
        {
            // Un Accept que no admite JSON (un navegador) hace que el servicio no escriba; el contrato sigue siendo el mismo cuerpo.
            await Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", Context.RequestAborted);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "API key rejected: {Rejection} (prefix {KeyPrefix}).")]
    private static partial void LogRejected(ILogger logger, ApiKeyRejection? rejection, string? keyPrefix);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "API key rejected: {Rejection} (prefix {KeyPrefix}).")]
    private static partial void LogRejectedNoise(ILogger logger, ApiKeyRejection? rejection, string? keyPrefix);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not update last_used_at for API key with prefix {KeyPrefix}.")]
    private static partial void LogTouchFailed(ILogger logger, Exception exception, string keyPrefix);
}
