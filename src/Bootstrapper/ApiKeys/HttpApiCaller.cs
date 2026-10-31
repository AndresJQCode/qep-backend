using System.Security.Claims;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Quien llama con una clave de API, leído del principal que armó <see cref="ApiKeyAuthenticationHandler"/>. Toma solo la
/// identidad del esquema <c>ApiKey</c>: una cookie de sesión presente en la misma petición no cuenta. Sin esa identidad falla
/// cerrado (sin scopes, sin conexiones), nunca abierto.
/// </summary>
internal sealed class HttpApiCaller(IHttpContextAccessor accessor) : IApiCaller
{
    private ClaimsIdentity? Identity =>
        accessor.HttpContext?.User.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated && identity.AuthenticationType == ApiKeyDefaults.AuthenticationScheme);

    public Guid ApiKeyId => GuidClaim(ApiKeyClaimTypes.KeyId);

    public string ApiKeyName => Identity?.FindFirst(ApiKeyClaimTypes.KeyName)?.Value ?? string.Empty;

    public Guid TenantId => GuidClaim(QepClaimTypes.TenantId);

    public bool HasScope(string scope) =>
        Identity?.HasClaim(ApiKeyClaimTypes.Scope, scope) ?? false;

    // Sin claims de conexión la clave no está restringida (connection_ids NULL); una clave con lista nunca la tiene vacía (CK_api_keys_connections).
    public bool CanUseConnection(Guid connectionId)
    {
        var identity = Identity;
        if (identity is null)
        {
            return false;
        }

        var allowed = identity.FindAll(ApiKeyClaimTypes.Connection).ToArray();
        return allowed.Length == 0 || allowed.Any(claim => Guid.TryParse(claim.Value, out var id) && id == connectionId);
    }

    private Guid GuidClaim(string type) =>
        Guid.TryParse(Identity?.FindFirst(type)?.Value, out var value) ? value : Guid.Empty;
}
