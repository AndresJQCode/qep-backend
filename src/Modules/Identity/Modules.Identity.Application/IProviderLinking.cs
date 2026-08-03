namespace Modules.Identity.Application;

/// <summary>
/// Outcome of an external-login link attempt. Exactly one of <see cref="UserId"/> or
/// <see cref="DenialReason"/> is set.
/// </summary>
public sealed record ProviderLinkOutcome(Guid? UserId, string? DenialReason)
{
    public static ProviderLinkOutcome Linked(Guid userId) => new(userId, null);

    public static ProviderLinkOutcome Denied(string reason) => new(null, reason);

    public bool IsDenied => DenialReason is not null;
}

/// <summary>
/// Published cross-module contract that resolves an external provider identity to an
/// internal user, applying the ADR 0015 rules: invitation-only provisioning, link on
/// a verified-email match, deny for unknown or unverified emails. Used by the
/// composition-root <c>/auth/session</c> endpoint on first login.
/// </summary>
public interface IProviderLinking
{
    Task<ProviderLinkOutcome> LinkAndActivateAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken);
}
