using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed class ProviderLinkingService(
    IUserRepository userRepository,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IProviderLinking
{
    public async Task<ProviderLinkOutcome> LinkAndActivateAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken)
    {
        // Already linked: the external subject maps to a known user. Nothing to
        // provision; the user is returned as-is.
        var linked = await userRepository.FindByProviderAsync(provider, subject, cancellationToken);
        if (linked is not null)
        {
            return ProviderLinkOutcome.Linked(linked.Id.Value);
        }

        // First login for this subject. Per ADR 0015 only a verified email may be
        // used to match an invited user.
        if (!emailVerified)
        {
            return ProviderLinkOutcome.Denied("email_not_verified");
        }

        string normalizedEmail;
        try
        {
            normalizedEmail = User.NormalizeEmail(email ?? string.Empty);
        }
        catch (IdentityDomainException)
        {
            return ProviderLinkOutcome.Denied("email_invalid");
        }

        // Invitation-only: an unknown email is never auto-provisioned.
        var user = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null)
        {
            return ProviderLinkOutcome.Denied("invitation_required");
        }

        var now = clock.UtcNow;
        user.LinkProvider(provider, subject, now);
        user.Activate(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProviderLinkOutcome.Linked(user.Id.Value);
    }
}
