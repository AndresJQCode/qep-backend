namespace Modules.ApiAccess.Domain;

public sealed class ApiKey
{
    public const int NameMaxLength = 80;
    public const int PrefixLength = 8;
    public const int MaxActivePerTenant = 25;

    public static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromDays(30);

    // Para EF Core.
    private ApiKey()
    {
        Name = string.Empty;
        KeyPrefix = string.Empty;
        SecretHash = [];
        Scopes = [];
    }

    private ApiKey(
        ApiKeyId id,
        Guid tenantId,
        string name,
        string keyPrefix,
        byte[] secretHash,
        string[] scopes,
        Guid[]? connectionIds,
        DateTimeOffset? expiresAt,
        Guid createdByMemberId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        KeyPrefix = keyPrefix;
        SecretHash = secretHash;
        Scopes = scopes;
        ConnectionIds = connectionIds;
        ExpiresAt = expiresAt;
        CreatedByMemberId = createdByMemberId;
        CreatedAt = createdAt;
    }

    public ApiKeyId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string KeyPrefix { get; private set; }

    public byte[] SecretHash { get; private set; }

    public string[] Scopes { get; private set; }

    /// <summary><c>null</c> es «todas las conexiones»; una lista nunca es vacía.</summary>
    public Guid[]? ConnectionIds { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByMemberId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedByMemberId { get; private set; }

    public int Version { get; private set; }

    public static ApiKey Create(
        Guid tenantId,
        string name,
        ApiKeyMaterial material,
        IReadOnlyCollection<string> scopes,
        IReadOnlyCollection<Guid>? connectionIds,
        DateTimeOffset? expiresAt,
        Guid createdByMemberId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(scopes);

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ApiAccessDomainException(
                "api_access.name_required",
                "Ponle un nombre a la clave.");
        }

        if (trimmed.Length > NameMaxLength)
        {
            throw new ApiAccessDomainException(
                "api_access.name_too_long",
                $"El nombre de la clave no puede pasar de {NameMaxLength} caracteres.");
        }

        if (scopes.Count == 0)
        {
            throw new ApiAccessDomainException(
                "api_access.scopes_required",
                "Elige al menos un permiso para la clave.");
        }

        if (connectionIds is { Count: 0 })
        {
            throw new ApiAccessDomainException(
                "api_access.connections_empty",
                "Elige al menos una conexión, o deja la clave sin restricción de conexiones.");
        }

        return new ApiKey(
            ApiKeyId.New(),
            tenantId,
            trimmed,
            material.Prefix,
            material.SecretHash,
            scopes.Distinct(StringComparer.Ordinal).ToArray(),
            connectionIds?.Distinct().ToArray(),
            expiresAt,
            createdByMemberId,
            now);
    }

    /// <returns><c>true</c> si cambió; una clave ya revocada conserva su primera revocación.</returns>
    public bool Revoke(Guid memberId, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        RevokedByMemberId = memberId;
        Version++;
        return true;
    }

    public ApiKeyStatus StatusAt(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return ApiKeyStatus.Revoked;
        }

        if (ExpiresAt is null)
        {
            return ApiKeyStatus.Active;
        }

        if (ExpiresAt.Value <= now)
        {
            return ApiKeyStatus.Expired;
        }

        return ExpiresAt.Value - now <= ExpiringSoonWindow
            ? ApiKeyStatus.ExpiringSoon
            : ApiKeyStatus.Active;
    }

    public bool IsUsableAt(DateTimeOffset now) =>
        StatusAt(now) is ApiKeyStatus.Active or ApiKeyStatus.ExpiringSoon;
}
