namespace BuildingBlocks.Application;

/// <summary>
/// Un permiso que un módulo ofrece a las claves de API. <c>RequiredModule</c> es texto porque
/// BuildingBlocks no ve Tenancy; la prueba de la composición lo amarra a <c>TenantModuleKeys</c>.
/// </summary>
public sealed record ApiScopeDefinition(
    string Key,
    string Name,
    string Description,
    string Category,
    string RequiredModule,
    bool RestrictableByConnection);

/// <summary>Quien llama con una clave de API, ya autenticado.</summary>
public interface IApiCaller
{
    Guid ApiKeyId { get; }

    string ApiKeyName { get; }

    Guid TenantId { get; }

    bool HasScope(string scope);

    bool CanUseConnection(Guid connectionId);
}

public static class ApiKeyDefaults
{
    public const string AuthenticationScheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

public static class ApiKeyClaimTypes
{
    public const string KeyId = "qep_api_key_id";
    public const string KeyName = "qep_api_key_name";
    public const string Scope = "qep_api_scope";
    public const string Connection = "qep_api_connection";
}

public static class ExternalApiErrorCodes
{
    public const string InvalidKey = "external.auth.invalid_key";
    public const string ScopeMissing = "external.auth.scope_missing";
    public const string ConnectionNotAllowed = "external.connection_not_allowed";
    public const string RateLimited = "external.rate_limited";
}
