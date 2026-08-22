namespace Modules.Identity.Domain;

/// <summary>
/// Identidad interna de una persona que puede autenticarse. Según el ADR 0015 un usuario
/// existe sólo después de ser invitado a un tenant (aprovisionamiento sólo por invitación);
/// los logins externos nunca crean usuarios solos. Identity es dueño del usuario, su estado
/// y sus vínculos con proveedores; no es dueño de la membresía ni evalúa autorización.
/// </summary>
public sealed class User
{
    private readonly List<ProviderLink> _providerLinks = [];

    private User()
    {
    }

    private User(UserId id, string email, UserStatus status, DateTimeOffset createdAt)
    {
        Id = id;
        Email = NormalizeEmail(email);
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public UserId Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<ProviderLink> ProviderLinks => _providerLinks.AsReadOnly();

    /// <summary>
    /// Crea un usuario en <see cref="UserStatus.Invited"/>. Es el único punto de
    /// entrada para un usuario nuevo; la activación pasa en el primer login externo
    /// exitoso con un email verificado que coincida.
    /// </summary>
    public static User CreateInvited(UserId id, string email, DateTimeOffset createdAt) =>
        new(id, email, UserStatus.Invited, createdAt);

    /// <summary>
    /// Pasa un usuario invitado a <see cref="UserStatus.Active"/>. Es idempotente para
    /// un usuario que ya está activo.
    /// </summary>
    public void Activate(DateTimeOffset occurredAt)
    {
        if (Status == UserStatus.Active)
        {
            return;
        }

        Status = UserStatus.Active;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Vincula el subject de un proveedor externo a este usuario. Según el ADR 0015 el
    /// llamador ya tiene que haber probado que el email es suyo (<c>email_verified</c> de Google).
    /// Es idempotente: volver a vincular el mismo (proveedor, subject) no hace nada; un
    /// subject distinto para el mismo proveedor se rechaza.
    /// </summary>
    public ProviderLink LinkProvider(string provider, string subject, DateTimeOffset occurredAt)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedSubject = ValidateSubject(subject);

        var existing = _providerLinks.SingleOrDefault(link =>
            StringComparer.Ordinal.Equals(link.Provider, normalizedProvider));
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.Subject, normalizedSubject))
            {
                throw new IdentityDomainException(
                    "identity.provider_link.conflict",
                    "The user is already linked to a different subject for this provider.");
            }

            return existing;
        }

        var link = ProviderLink.Create(Id, normalizedProvider, normalizedSubject, occurredAt);
        _providerLinks.Add(link);
        UpdatedAt = occurredAt;
        return link;
    }

    public static string NormalizeEmail(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 254 ||
            normalized.Count(character => character == '@') != 1 ||
            normalized.StartsWith('@') ||
            normalized.EndsWith('@'))
        {
            throw new IdentityDomainException(
                "identity.email.invalid",
                "Email must be a single valid address.");
        }

        return normalized;
    }

    private static string NormalizeProvider(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 50)
        {
            throw new IdentityDomainException(
                "identity.provider.invalid",
                "Provider must be a non-empty identifier of at most 50 characters.");
        }

        return normalized;
    }

    private static string ValidateSubject(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 255)
        {
            throw new IdentityDomainException(
                "identity.subject.invalid",
                "Provider subject must be a non-empty value of at most 255 characters.");
        }

        return normalized;
    }
}
