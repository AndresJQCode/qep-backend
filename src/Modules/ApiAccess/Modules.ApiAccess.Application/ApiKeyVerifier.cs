using BuildingBlocks.Application;
using Modules.ApiAccess.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ApiAccess.Application;

/// <summary>
/// Por qué se rechazó una clave. Va al log con el prefijo; hacia fuera todas las causas son el mismo 401
/// (<c>external.auth.invalid_key</c>), para que quien prueba claves no aprenda cuál existe.
/// </summary>
public enum ApiKeyRejection
{
    Missing,
    Malformed,
    Unknown,
    SecretMismatch,
    Revoked,
    Expired,
    TenantNotActive,
}

/// <summary>Una clave ya validada: lo que el principal de la petición necesita saber de ella.</summary>
public sealed record VerifiedApiKey(
    Guid Id,
    string Name,
    Guid TenantId,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid>? ConnectionIds);

/// <param name="Key">La clave válida, o <c>null</c> si se rechazó.</param>
/// <param name="Rejection">La causa del rechazo, o <c>null</c> si pasó.</param>
/// <param name="KeyPrefix">El prefijo de una clave con forma válida, para el log. Nunca el secreto. Una clave malformada no lo trae.</param>
public sealed record ApiKeyVerification(VerifiedApiKey? Key, ApiKeyRejection? Rejection, string? KeyPrefix);

public interface IApiKeyVerifier
{
    Task<ApiKeyVerification> VerifyAsync(string? presentedKey, CancellationToken cancellationToken);
}

/// <summary>
/// Valida la clave que presenta un sistema externo. Lee en base en <b>cada</b> petición y sin caché: una caché es una ventana en
/// la que una clave revocada sigue viva (spec 2026-10-30 §4.3).
/// </summary>
public sealed class ApiKeyVerifier(IApiKeyRepository keys, ITenantDirectory tenants, IClock clock) : IApiKeyVerifier
{
    public async Task<ApiKeyVerification> VerifyAsync(string? presentedKey, CancellationToken cancellationToken)
    {
        if (presentedKey is null)
        {
            return Rejected(ApiKeyRejection.Missing, null);
        }

        // Sin prefijo: una cadena que no tiene la forma de una clave podría ser cualquier cosa, incluso un secreto pegado donde no va.
        if (!ApiKeyMaterial.TryParse(presentedKey, out var prefix, out var secret))
        {
            return Rejected(ApiKeyRejection.Malformed, null);
        }

        var key = await keys.FindByPrefixAsync(prefix, cancellationToken);
        if (key is null)
        {
            return Rejected(ApiKeyRejection.Unknown, prefix);
        }

        if (!ApiKeyMaterial.Matches(secret, key.SecretHash))
        {
            return Rejected(ApiKeyRejection.SecretMismatch, prefix);
        }

        if (key.RevokedAt is not null)
        {
            return Rejected(ApiKeyRejection.Revoked, prefix);
        }

        if (key.ExpiresAt is { } expiresAt && expiresAt <= clock.UtcNow)
        {
            return Rejected(ApiKeyRejection.Expired, prefix);
        }

        // Un tenant sin fila (nulo) tampoco pasa: una clave nunca se apoya en el tenant simulado del stub.
        var status = await tenants.GetStatusAsync(new TenantId(key.TenantId), cancellationToken);
        if (status != TenantStatus.Active)
        {
            return Rejected(ApiKeyRejection.TenantNotActive, prefix);
        }

        return new ApiKeyVerification(
            new VerifiedApiKey(key.Id.Value, key.Name, key.TenantId, key.KeyPrefix, key.Scopes, key.ConnectionIds),
            null,
            prefix);
    }

    private static ApiKeyVerification Rejected(ApiKeyRejection rejection, string? prefix) => new(null, rejection, prefix);
}
