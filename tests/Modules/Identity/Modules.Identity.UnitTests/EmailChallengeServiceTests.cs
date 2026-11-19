using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §4: el distribuidor nuevo confirma su correo con un código de un solo uso antes de
/// que exista su usuario. Identity guarda sólo el hash del código y del correo; el código crudo viaja únicamente en el evento
/// del outbox que lo entrega.
/// </summary>
public sealed class EmailChallengeServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryEmailChallengeRepository challenges = new();
    private readonly RecordingIdentityOutbox outbox = new();
    private readonly CountingIdentityUnitOfWork unitOfWork = new();
    private readonly MutableClock clock = new(Start);
    private readonly EmailChallengeService service;

    public EmailChallengeServiceTests() =>
        service = new EmailChallengeService(challenges, outbox, unitOfWork, clock);

    [Fact]
    public async Task IssueStoresOnlyTheHashOfTheCode()
    {
        await service.IssueAsync("  Person@Example.com ", TestContext.Current.CancellationToken);

        var challenge = Assert.Single(challenges.Added);
        var (eventName, payload) = Assert.Single(outbox.Added);
        var requested = Assert.IsType<EmailChallengeRequestedPayload>(payload);

        Assert.Equal(IdentityIntegrationEvents.EmailChallengeRequested, eventName);
        Assert.Equal("identity.email-challenge-requested.v1", eventName);
        Assert.Matches("^[0-9]{6}$", requested.Code);
        Assert.Equal(EmailChallengeService.CodeLength, requested.Code.Length);
        Assert.Equal("person@example.com", requested.Email);
        Assert.Equal(challenge.Id.Value, requested.ChallengeId);
        Assert.Equal(Start + EmailChallengeService.Lifetime, requested.ExpiresAt);

        // Lo que se guarda: el SHA-256 del código y del correo normalizado, nunca los valores crudos.
        Assert.Equal(EmailTokenCodec.Hash(requested.Code), challenge.CodeHash);
        Assert.Equal(EmailTokenCodec.Hash("person@example.com"), challenge.EmailHash);
        Assert.NotEqual(requested.Code, challenge.CodeHash);
        Assert.Equal(Start + EmailChallengeService.Lifetime, challenge.ExpiresAt);
        Assert.Equal(0, challenge.Attempts);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task IssuingAgainWithin60SecondsDoesNotPublishASecondEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await service.IssueAsync("person@example.com", ct);

        clock.UtcNow = Start + EmailChallengeService.ReissueInterval - TimeSpan.FromSeconds(1);
        await service.IssueAsync("Person@example.com", ct);

        Assert.Single(outbox.Added);
        Assert.Single(challenges.Added);
        Assert.Null(challenges.Added[0].UsedAt);

        // Pasado el intervalo sale un código nuevo y el anterior queda invalidado.
        clock.UtcNow = Start + EmailChallengeService.ReissueInterval;
        await service.IssueAsync("person@example.com", ct);

        Assert.Equal(2, outbox.Added.Count);
        Assert.Equal(2, challenges.Added.Count);
        Assert.Equal(clock.UtcNow, challenges.Added[0].UsedAt);
        Assert.Null(challenges.Added[1].UsedAt);
    }

    [Fact]
    public async Task TheSixthChallengeInADayIsNotIssued()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var issue = 0; issue < EmailChallengeService.MaxIssuesPerDay; issue++)
        {
            clock.UtcNow = Start + (EmailChallengeService.ReissueInterval * issue);
            await service.IssueAsync("person@example.com", ct);
        }

        Assert.Equal(5, outbox.Added.Count);

        // El sexto, ya fuera del intervalo de 60 s: no se emite y no falla, igual que dentro del intervalo.
        clock.UtcNow = Start + (EmailChallengeService.ReissueInterval * EmailChallengeService.MaxIssuesPerDay);
        await service.IssueAsync("person@example.com", ct);

        Assert.Equal(5, outbox.Added.Count);
        Assert.Equal(5, challenges.Added.Count);
        Assert.All(challenges.Locks, handle => Assert.True(handle.Disposed));

        // La ventana es móvil: 24 h después del primero vuelve a haber cupo para uno.
        clock.UtcNow = Start + EmailChallengeService.IssueWindow;
        await service.IssueAsync("person@example.com", ct);

        Assert.Equal(6, outbox.Added.Count);
    }

    [Fact]
    public async Task IssuingRunsUnderThePerEmailLockAndCommitsOnlyWhenItIssues()
    {
        var ct = TestContext.Current.CancellationToken;
        await service.IssueAsync("person@example.com", ct);
        await service.IssueAsync("person@example.com", ct);

        Assert.Equal(2, challenges.Locks.Count);
        Assert.All(challenges.Locks, handle => Assert.Equal(EmailTokenCodec.Hash("person@example.com"), handle.EmailHash));
        Assert.Equal([true, false], challenges.Locks.Select(handle => handle.Committed));
        Assert.All(challenges.Locks, handle => Assert.True(handle.Disposed));
    }

    [Fact]
    public void TheConstantsAreTheOnesTheSpecFixes()
    {
        Assert.Equal(6, EmailChallengeService.CodeLength);
        Assert.Equal(TimeSpan.FromMinutes(10), EmailChallengeService.Lifetime);
        Assert.Equal(5, EmailChallengeService.MaxAttempts);
        Assert.Equal(5, EmailChallengeService.MaxIssuesPerDay);
        Assert.Equal(TimeSpan.FromHours(24), EmailChallengeService.IssueWindow);
        Assert.Equal(TimeSpan.FromSeconds(60), EmailChallengeService.ReissueInterval);
    }

    [Fact]
    public async Task TryConsumeComparesTheHashesOfTheNormalizedEmailAndTheCode()
    {
        challenges.ConsumeResult = true;

        var consumed = await service.TryConsumeAsync(" Person@Example.com", " 123456 ", TestContext.Current.CancellationToken);

        Assert.True(consumed);
        var call = Assert.Single(challenges.ConsumeCalls);
        Assert.Equal(EmailTokenCodec.Hash("person@example.com"), call.EmailHash);
        Assert.Equal(EmailTokenCodec.Hash("123456"), call.CodeHash);
        Assert.Equal(EmailChallengeService.MaxAttempts, call.MaxAttempts);
        Assert.Equal(Start, call.Now);
        // Atómico y fuera de la unidad de trabajo, como IEmailTokenRepository.TryConsumeAsync.
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task AMalformedEmailIsNeitherIssuedNorConsumed()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<IdentityDomainException>(() => service.IssueAsync("not-an-email", ct));
        Assert.False(await service.TryConsumeAsync("not-an-email", "123456", ct));

        Assert.Empty(outbox.Added);
        Assert.Empty(challenges.ConsumeCalls);
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class InMemoryEmailChallengeRepository : IEmailChallengeRepository
    {
        public List<EmailChallenge> Added { get; } = [];

        public List<(string EmailHash, string CodeHash, int MaxAttempts, DateTimeOffset Now)> ConsumeCalls { get; } = [];

        public bool ConsumeResult { get; set; }

        public List<RecordingIssuanceLock> Locks { get; } = [];

        public void Add(EmailChallenge challenge) => Added.Add(challenge);

        public Task<IEmailChallengeIssuanceLock> LockIssuanceAsync(string emailHash, CancellationToken cancellationToken)
        {
            var handle = new RecordingIssuanceLock(emailHash);
            Locks.Add(handle);
            return Task.FromResult<IEmailChallengeIssuanceLock>(handle);
        }

        public Task<IReadOnlyList<DateTimeOffset>> ListIssuedSinceAsync(
            string emailHash, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DateTimeOffset>>(Added
                .Where(challenge => challenge.EmailHash == emailHash && challenge.CreatedAt > since)
                .Select(challenge => challenge.CreatedAt)
                .ToList());

        public Task InvalidateUnusedAsync(string emailHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            foreach (var challenge in Added.Where(challenge => challenge.EmailHash == emailHash))
            {
                challenge.Invalidate(now);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryConsumeAsync(
            string emailHash, string codeHash, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ConsumeCalls.Add((emailHash, codeHash, maxAttempts, now));
            return Task.FromResult(ConsumeResult);
        }
    }

    private sealed class RecordingIssuanceLock(string emailHash) : IEmailChallengeIssuanceLock
    {
        public string EmailHash { get; } = emailHash;

        public bool Committed { get; private set; }

        public bool Disposed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
