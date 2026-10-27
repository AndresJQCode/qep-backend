using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class PasswordCredentialRepository(IdentityDbContext dbContext)
    : IPasswordCredentialRepository
{
    public Task<PasswordCredential?> FindByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.PasswordCredentials.SingleOrDefaultAsync(
            credential => credential.UserId == new UserId(userId),
            cancellationToken);

    public void Add(PasswordCredential credential) => dbContext.PasswordCredentials.Add(credential);

    public void Remove(PasswordCredential credential) => dbContext.PasswordCredentials.Remove(credential);
}
