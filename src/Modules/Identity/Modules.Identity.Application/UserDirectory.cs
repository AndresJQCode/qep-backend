using Modules.Identity.Domain;

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

    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        // La misma normalización con que se guarda: sin ella, " Ana@X.CO " no encontraría a "ana@x.co".
        string normalized;
        try
        {
            normalized = User.NormalizeEmail(email);
        }
        catch (IdentityDomainException)
        {
            return null;
        }

        var user = await userRepository.FindByEmailAsync(normalized, cancellationToken);
        return user?.Id.Value;
    }
}
