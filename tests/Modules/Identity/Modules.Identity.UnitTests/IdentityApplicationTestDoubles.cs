using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

internal sealed class FakeEmailTokenRepository : IEmailTokenRepository
{
    public List<EmailToken> Added { get; } = [];

    public List<(Guid UserId, EmailTokenPurpose? Purpose)> Invalidations { get; } = [];

    public void Add(EmailToken token) => Added.Add(token);

    public Task InvalidateUnusedAsync(
        Guid userId,
        EmailTokenPurpose? purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Invalidations.Add((userId, purpose));
        return Task.CompletedTask;
    }

    public Task<Guid?> TryConsumeAsync(
        string tokenHash,
        EmailTokenPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

internal sealed class FakePasswordCredentialRepository : IPasswordCredentialRepository
{
    public List<PasswordCredential> Stored { get; } = [];

    public List<PasswordCredential> Added { get; } = [];

    public List<PasswordCredential> Removed { get; } = [];

    public Task<PasswordCredential?> FindByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.FirstOrDefault(credential => credential.UserId.Value == userId));

    public void Add(PasswordCredential credential) => Added.Add(credential);

    public void Remove(PasswordCredential credential) => Removed.Add(credential);
}

internal sealed class FakeUserRepository : IUserRepository
{
    public List<User> Stored { get; } = [];

    public List<User> Added { get; } = [];

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.FirstOrDefault(user => user.Email == normalizedEmail));

    public Task<User?> FindByProviderAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken) =>
        Task.FromResult(Stored.FirstOrDefault(user => user.ProviderLinks.Any(link =>
            link.Provider == provider && link.Subject == subject)));

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.FirstOrDefault(user => user.Id.Value == userId));

    public void Add(User user) => Added.Add(user);

    public void Remove(User user) => Stored.Remove(user);
}

internal sealed class CountingIdentityUnitOfWork : IIdentityUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(0);
    }
}
