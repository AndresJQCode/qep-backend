using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed class OwnerProvisioningService(
    IUserRepository userRepository,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IOwnerProvisioning
{
    public async Task<Guid> ProvisionOwnerAsync(
        string provider,
        string subject,
        string email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);
        var now = clock.UtcNow;

        var user = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = User.CreateInvited(UserId.New(), normalizedEmail, now);
            userRepository.Add(user);
        }

        user.Activate(now);
        user.LinkProvider(provider, subject, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return user.Id.Value;
    }
}
