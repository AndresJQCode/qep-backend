using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed class IdentityProvisioningService(
    IUserRepository userRepository,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IIdentityProvisioning
{
    public async Task<Guid> GetOrProvisionInvitedUserAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);

        var existing = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return existing.Id.Value;
        }

        var user = User.CreateInvited(UserId.New(), normalizedEmail, clock.UtcNow);
        userRepository.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return user.Id.Value;
    }
}
