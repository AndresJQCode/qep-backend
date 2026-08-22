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
        // Ya vinculado: el subject externo mapea a un usuario conocido. Nada que
        // aprovisionar; el usuario se devuelve como está.
        var linked = await userRepository.FindByProviderAsync(provider, subject, cancellationToken);
        if (linked is not null)
        {
            return ProviderLinkOutcome.Linked(linked.Id.Value);
        }

        // Primer login para este subject. Según el ADR 0015 sólo un email verificado puede
        // usarse para hacer match con un usuario invitado.
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

        // Sólo por invitación: un email desconocido nunca se aprovisiona solo.
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
