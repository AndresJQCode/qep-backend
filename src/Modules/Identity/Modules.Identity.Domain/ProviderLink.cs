namespace Modules.Identity.Domain;

/// <summary>
/// Links an external identity provider subject to an internal user. The pair
/// (<see cref="Provider"/>, <see cref="Subject"/>) is globally unique: an external
/// subject maps to exactly one user. Per ADR 0015 the subject is never the internal
/// user id.
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
