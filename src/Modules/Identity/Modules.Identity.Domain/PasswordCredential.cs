namespace Modules.Identity.Domain;

/// <summary>
/// Credencial de contraseña de un usuario: guarda sólo el hash que produce el puerto
/// <c>IPasswordHasher</c>, nunca la contraseña. Es opcional: un usuario que sólo entra por un
/// proveedor externo no tiene fila.
/// </summary>
public sealed class PasswordCredential
{
    private PasswordCredential()
    {
    }

    private PasswordCredential(UserId userId, string hash, DateTimeOffset createdAt)
    {
        UserId = userId;
        Hash = hash;
        UpdatedAt = createdAt;
    }

    public UserId UserId { get; private set; }

    public string Hash { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PasswordCredential Create(UserId userId, string hash, DateTimeOffset createdAt) =>
        new(userId, RequireHash(hash), createdAt);

    public void Replace(string hash, DateTimeOffset occurredAt)
    {
        Hash = RequireHash(hash);
        UpdatedAt = occurredAt;
    }

    private static string RequireHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new IdentityDomainException(
                "identity.password.hash_required",
                "A password credential needs a non-empty hash.");
        }

        return hash;
    }
}
