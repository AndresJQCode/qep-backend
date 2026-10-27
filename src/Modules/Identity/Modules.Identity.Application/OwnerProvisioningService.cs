using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed class OwnerProvisioningService(
    IUserRepository userRepository,
    IVerifiedProviderLinker providerLinker,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IOwnerProvisioning
{
    public async Task<ProviderLinkOutcome> ProvisionOwnerAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken)
    {
        // Un email vacío o sin verificar nunca crea ni toca un usuario (D11).
        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
        {
            return ProviderLinkOutcome.Denied("email_not_verified");
        }

        var normalizedEmail = User.NormalizeEmail(email);
        var now = clock.UtcNow;

        var user = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = User.CreateInvited(UserId.New(), normalizedEmail, now);
            userRepository.Add(user);
        }

        user.Activate(now);
        await providerLinker.LinkAsync(user, provider, subject, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProviderLinkOutcome.Linked(user.Id.Value);
    }
}
