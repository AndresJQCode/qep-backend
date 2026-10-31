using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.Application;

public sealed record ApiKeyScopeDto(string Key, string Name);

/// <summary><c>Name</c> nulo: la conexión ya no existe. La clave la conserva en su lista y la pantalla lo avisa.</summary>
public sealed record ApiKeyConnectionDto(Guid Id, string? Name);

/// <summary>
/// Nunca lleva el hash ni el secreto. <c>Connections</c> nulo es «todos los números»: una lista
/// vacía no existe (el dominio la rechaza), así que la pantalla no tiene que distinguir los dos casos.
/// <c>Status</c> viaja con su nombre (<c>Active</c>, <c>ExpiringSoon</c>, <c>Expired</c>, <c>Revoked</c>)
/// y se calcula en el servidor: el cliente no tiene que comparar fechas contra su reloj.
/// </summary>
public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<ApiKeyScopeDto> Scopes,
    IReadOnlyList<ApiKeyConnectionDto>? Connections,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    string Status);

/// <summary><c>Secret</c> es la clave completa, y esta es la única respuesta que la trae.</summary>
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret)
{
    // Un log o el depurador llaman a ToString: nunca puede llevar la clave.
    public override string ToString() => $"CreatedApiKeyDto {{ Key = {Key.Id} }}";
}

public sealed record ApiScopeDto(string Key, string Name, string Description, bool RestrictableByConnection);

public sealed record ApiScopeCategoryDto(string Category, IReadOnlyList<ApiScopeDto> Scopes);

public sealed record ApiScopeCatalogDto(IReadOnlyList<ApiScopeCategoryDto> Categories);

internal static class ApiKeyMapping
{
    public static ApiKeyDto ToDto(
        this ApiKey key,
        IReadOnlyDictionary<string, BuildingBlocks.Application.ApiScopeDefinition> scopeCatalog,
        IReadOnlyDictionary<Guid, string> connectionNames,
        DateTimeOffset now) => new(
        key.Id.Value,
        key.Name,
        key.KeyPrefix,
        key.Scopes
            .Select(scope => new ApiKeyScopeDto(
                scope,
                scopeCatalog.TryGetValue(scope, out var definition) ? definition.Name : scope))
            .ToArray(),
        key.ConnectionIds?
            .Select(id => new ApiKeyConnectionDto(id, connectionNames.GetValueOrDefault(id)))
            .ToArray(),
        key.LastUsedAt,
        key.ExpiresAt,
        key.CreatedAt,
        key.RevokedAt,
        key.StatusAt(now).ToString());
}
