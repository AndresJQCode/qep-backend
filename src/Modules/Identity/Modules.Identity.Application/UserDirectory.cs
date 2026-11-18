namespace Modules.Identity.Application;

public sealed class UserDirectory(IUserRepository userRepository) : IUserDirectory
{
    public async Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        return user?.Email;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetEmailsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var users = await userRepository.ListByIdsAsync(userIds.Distinct().ToList(), cancellationToken);
        return users.ToDictionary(user => user.Id.Value, user => user.Email);
    }
}
