namespace Modules.Identity.Domain;

/// <summary>
/// Vincula el subject de un proveedor de identidad externo con un usuario interno. El par
/// (<see cref="Provider"/>, <see cref="Subject"/>) es único a nivel global: un subject
/// externo mapea a exactamente un usuario. Según el ADR 0015 el subject nunca es el id
/// interno de usuario.
/// </summary>
public sealed class ProviderLink
{
    private ProviderLink()
    {
    }

    private ProviderLink(
        Guid id,
        UserId userId,
        string provider,
        string subject,
        DateTimeOffset linkedAt)
    {
        Id = id;
        UserId = userId;
        Provider = provider;
        Subject = subject;
        LinkedAt = linkedAt;
    }

    public Guid Id { get; private set; }

    public UserId UserId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public DateTimeOffset LinkedAt { get; private set; }

    internal static ProviderLink Create(
        UserId userId,
        string provider,
        string subject,
        DateTimeOffset linkedAt) =>
        new(Guid.CreateVersion7(), userId, provider, subject, linkedAt);
}
