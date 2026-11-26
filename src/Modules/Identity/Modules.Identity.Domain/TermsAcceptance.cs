namespace Modules.Identity.Domain;

public readonly record struct TermsAcceptanceId(Guid Value)
{
    public static TermsAcceptanceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Spec 2026-11-19 (onboarding por link): quién aceptó los términos, cuándo y qué versión. Una fila por usuario y versión; una
/// versión nueva de los términos es otra fila, no una edición de la anterior.
/// </summary>
public sealed class TermsAcceptance
{
    public const int VersionMaxLength = 50;

    private TermsAcceptance()
    {
    }

    private TermsAcceptance(TermsAcceptanceId id, UserId userId, string version, DateTimeOffset acceptedAt)
    {
        Id = id;
        UserId = userId;
        Version = version;
        AcceptedAt = acceptedAt;
    }

    public TermsAcceptanceId Id { get; private set; }

    public UserId UserId { get; private set; }

    public string Version { get; private set; } = string.Empty;

    public DateTimeOffset AcceptedAt { get; private set; }

    public static TermsAcceptance Accept(TermsAcceptanceId id, UserId userId, string version, DateTimeOffset acceptedAt)
    {
        var trimmed = version?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > VersionMaxLength)
        {
            throw new IdentityDomainException(
                "identity.terms.version_invalid",
                "A terms version must have between 1 and 50 characters.");
        }

        return new TermsAcceptance(id, userId, trimmed, acceptedAt);
    }
}
