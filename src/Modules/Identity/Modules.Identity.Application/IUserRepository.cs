using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IUserRepository
{
    /// <param name="normalizedEmail">Email ya normalizado con <see cref="User.NormalizeEmail"/>.</param>
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindByProviderAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    void Add(User user);
}
