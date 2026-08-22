using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> FindByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        dbContext.Users.SingleOrDefaultAsync(
            user => user.Email == normalizedEmail,
            cancellationToken);

    public Task<User?> FindByProviderAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken) =>
        dbContext.Users
            .Include(user => user.ProviderLinks)
            .SingleOrDefaultAsync(
                user => user.ProviderLinks.Any(link =>
                    link.Provider == provider && link.Subject == subject),
                cancellationToken);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == new UserId(userId),
            cancellationToken);

    public void Add(User user) => dbContext.Users.Add(user);
}
