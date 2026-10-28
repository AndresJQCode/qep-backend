using BuildingBlocks.Application;
using Modules.Audit.Domain;
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

internal sealed class FakePasswordHasher : IPasswordHasher
{
    public List<string?> VerifiedHashes { get; } = [];

    public PasswordVerificationOutcome NextOutcome { get; set; } = PasswordVerificationOutcome.Success;

    public string Hash(string password) => "rehashed";

    // A null hash is the "run against the dummy" call: it never succeeds.
    public PasswordVerificationOutcome Verify(string? hashedPassword, string providedPassword)
    {
        VerifiedHashes.Add(hashedPassword);
        return hashedPassword is null ? PasswordVerificationOutcome.Failed : NextOutcome;
    }
}

internal sealed class FakeEmailTokenIssuer : IEmailTokenIssuer
{
    public List<(Guid UserId, EmailTokenPurpose Purpose)> Issued { get; } = [];

    public Task<string> IssueAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken)
    {
        Issued.Add((userId, purpose));
        return Task.FromResult("raw-token");
    }
}

internal sealed class RecordingIdentityOutbox : IIdentityOutbox
{
    public List<(string EventName, object Payload)> Added { get; } = [];

    public void Add(string eventName, object payload, string correlationId, DateTimeOffset occurredAt) =>
        Added.Add((eventName, payload));
}

internal sealed class RecordingIdentityAuditRecorder : IIdentityAuditRecorder
{
    public List<(Guid ActorId, string Action, string ResourceId, AuditActorType ActorType)> Entries { get; } = [];

    public void Record(
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human) =>
        Entries.Add((actorId, action, resourceId, actorType));
}
