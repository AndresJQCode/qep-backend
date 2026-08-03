namespace Modules.Identity.Application;

public sealed class UserDirectory(IUserRepository userRepository) : IUserDirectory
{
    public async Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        return user?.Email;
    }
}
