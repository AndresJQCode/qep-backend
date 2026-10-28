using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IPasswordCredentialRepository
{
    Task<PasswordCredential?> FindByUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PasswordCredential credential);

    void Remove(PasswordCredential credential);
}
