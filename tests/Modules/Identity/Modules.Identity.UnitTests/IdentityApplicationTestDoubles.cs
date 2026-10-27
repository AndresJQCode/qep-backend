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
