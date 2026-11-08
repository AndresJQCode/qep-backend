using BuildingBlocks.Application;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed record QueuedEvent(string EventName, object Payload, DateTimeOffset OccurredAt);

/// <summary>
/// El almacén en memoria que comparten los dobles de los puertos de Buyers. Imita la unidad de trabajo real: lo que se
/// agrega queda pendiente hasta <see cref="IBuyersUnitOfWork.SaveChangesAsync"/>, y un conflicto de email o un descarte lo
/// borran.
/// </summary>
internal sealed class BuyersWorld
{
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly List<BuyerAccount> _pendingAccounts = [];
    private readonly List<BuyerProfile> _pendingProfiles = [];
    private readonly List<BuyerConsent> _pendingConsents = [];
    private readonly List<BuyerEmailToken> _pendingTokens = [];
    private readonly List<BuyerSession> _pendingSessions = [];
    private readonly List<QueuedEvent> _pendingEvents = [];

    public BuyersWorld()
    {
        Clock = new FakeClock(Now);
        Hasher = new CountingHasher();
        Accounts = new FakeAccounts(this);
        Profiles = new FakeProfiles(this);
        Consents = new FakeConsents(this);
        Tokens = new FakeTokens(this);
        Sessions = new FakeSessions(this);
        Outbox = new FakeOutbox(this);
        UnitOfWork = new FakeUnitOfWork(this);
        ProfileWriter = new BuyerProfileWriter(Profiles);
        TokenIssuer = new BuyerEmailTokenIssuer(Tokens, Outbox);
        Signup = new BuyerSignup(Accounts, Consents, ProfileWriter, TokenIssuer, Outbox, UnitOfWork, Clock);
    }

    public FakeClock Clock { get; }
    public CountingHasher Hasher { get; }
    public FakeAccounts Accounts { get; }
    public FakeProfiles Profiles { get; }
    public FakeConsents Consents { get; }
    public FakeTokens Tokens { get; }
    public FakeSessions Sessions { get; }
    public FakeOutbox Outbox { get; }
    public FakeUnitOfWork UnitOfWork { get; }
    public BuyerProfileWriter ProfileWriter { get; }
    public BuyerEmailTokenIssuer TokenIssuer { get; }
    public BuyerSignup Signup { get; }

    public List<BuyerAccount> SavedAccounts { get; } = [];
    public List<BuyerProfile> SavedProfiles { get; } = [];
    public List<BuyerConsent> SavedConsents { get; } = [];
    public List<BuyerEmailToken> SavedTokens { get; } = [];
    public List<BuyerSession> SavedSessions { get; } = [];
    public List<QueuedEvent> SavedEvents { get; } = [];

    public int SaveCalls { get; private set; }

    /// <summary>Corre en el próximo guardado y lo hace fallar con el conflicto de email: simula al ganador de la carrera.</summary>
    public Action? WinTheRaceOnNextSave { get; set; }

    /// <summary>Corre en el próximo guardado y lo hace fallar por concurrencia: simula otra escritura (o la purga) de la cuenta.</summary>
    public Action? ConflictOnNextSave { get; set; }

    public BuyerAccount AddVerifiedAccount(string email, string hash)
    {
        var account = BuyerAccount.Register(Guid.NewGuid(), email, hash, Now);
        account.MarkEmailVerified(Now);
        SavedAccounts.Add(account);
        return account;
    }

    public BuyerAccount AddUnverifiedAccount(string email, string hash)
    {
        var account = BuyerAccount.Register(Guid.NewGuid(), email, hash, Now);
        SavedAccounts.Add(account);
        return account;
    }

    public IReadOnlyList<QueuedEvent> EventsNamed(string name) => SavedEvents.Where(e => e.EventName == name).ToList();

    internal sealed class FakeAccounts(BuyersWorld world) : IBuyerAccountRepository
    {
        public Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
            Task.FromResult(world.SavedAccounts.SingleOrDefault(a => a.Email == normalizedEmail));

        public Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(world.SavedAccounts.SingleOrDefault(a => a.Id == id));

        public void Add(BuyerAccount account) => world._pendingAccounts.Add(account);

        public void Remove(BuyerAccount account) => world.SavedAccounts.Remove(account);
    }

    internal sealed class FakeProfiles(BuyersWorld world) : IBuyerProfileRepository
    {
        public Task<BuyerProfile?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(world.SavedProfiles.SingleOrDefault(p => p.AccountId == accountId));

        public void Add(BuyerProfile profile) => world._pendingProfiles.Add(profile);

        public void Remove(BuyerProfile profile) => world.SavedProfiles.Remove(profile);
    }

    internal sealed class FakeConsents(BuyersWorld world) : IBuyerConsentRepository
    {
        public void Add(BuyerConsent consent) => world._pendingConsents.Add(consent);
    }

    internal sealed class FakeTokens(BuyersWorld world) : IBuyerEmailTokenRepository
    {
        public void Add(BuyerEmailToken token) => world._pendingTokens.Add(token);

        public Task InvalidateUnusedAsync(
            Guid accountId, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
        {
            foreach (var token in world.SavedTokens.Where(t => t.AccountId == accountId && t.Purpose == purpose))
            {
                token.Invalidate(now);
            }

            return Task.CompletedTask;
        }

        public Task<Guid?> TryConsumeAsync(
            string tokenHash, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeSessions(BuyersWorld world) : IBuyerSessionRepository
    {
        /// <summary>Cuántas veces se consultó por hash: un token inservible no debe llegar aquí.</summary>
        public int FindCalls { get; private set; }

        public void Add(BuyerSession session) => world._pendingSessions.Add(session);

        public Task<BuyerSession?> FindByTokenHashAsync(string hash, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(world.SavedSessions.SingleOrDefault(s => s.TokenHash == hash));
        }

        public Task<IReadOnlyList<BuyerSession>> ListActiveAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BuyerSession>>(
                world.SavedSessions.Where(s => s.AccountId == accountId && s.RevokedAt is null).ToList());

        public Task RevokeActiveAsync(
            Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
        {
            foreach (var session in world.SavedSessions.Where(s => s.AccountId == accountId && s.Id != keepSessionId))
            {
                session.Revoke(now, reason);
            }

            return Task.CompletedTask;
        }
    }

    internal sealed class FakeOutbox(BuyersWorld world) : IBuyerOutbox
    {
        public void Add(string eventName, object payload, DateTimeOffset occurredAt) =>
            world._pendingEvents.Add(new QueuedEvent(eventName, payload, occurredAt));
    }

    internal sealed class FakeUnitOfWork(BuyersWorld world) : IBuyersUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            world.SaveCalls++;
            if (world.WinTheRaceOnNextSave is { } race)
            {
                world.WinTheRaceOnNextSave = null;
                race();
                DiscardPendingChanges();
                throw new ResourceConflictException(BuyerErrorCodes.EmailTaken, "Another buyer account already uses that email.");
            }

            if (world.ConflictOnNextSave is { } change)
            {
                world.ConflictOnNextSave = null;
                change();
                DiscardPendingChanges();
                throw new RequestConcurrencyException("concurrency.conflict", "The buyer account changed.");
            }

            world.SavedAccounts.AddRange(world._pendingAccounts);
            world.SavedProfiles.AddRange(world._pendingProfiles);
            world.SavedConsents.AddRange(world._pendingConsents);
            world.SavedTokens.AddRange(world._pendingTokens);
            world.SavedSessions.AddRange(world._pendingSessions);
            world.SavedEvents.AddRange(world._pendingEvents);
            DiscardPendingChanges();
            return Task.CompletedTask;
        }

        public async Task SaveChangesAsync(Func<CancellationToken, Task> afterSave, CancellationToken cancellationToken)
        {
            await SaveChangesAsync(cancellationToken);
            await afterSave(cancellationToken);
        }

        public void DiscardPendingChanges()
        {
            world._pendingAccounts.Clear();
            world._pendingProfiles.Clear();
            world._pendingConsents.Clear();
            world._pendingTokens.Clear();
            world._pendingSessions.Clear();
            world._pendingEvents.Clear();
        }
    }
}

/// <summary>Un hasher que cuenta las llamadas: el hash corre en todos los caminos del registro.</summary>
internal sealed class CountingHasher : IBuyerPasswordHasher
{
    public int HashCalls { get; private set; }

    public string Hash(string password)
    {
        HashCalls++;
        return $"hash::{password}";
    }

    public BuyerPasswordCheck Verify(string? hash, string password) =>
        hash == $"hash::{password}" ? BuyerPasswordCheck.Success : BuyerPasswordCheck.Failed;
}

/// <summary>Un hasher que dice que todo hash correcto está desactualizado.</summary>
internal sealed class RehashingHasher : IBuyerPasswordHasher
{
    public string Hash(string password) => $"new-hash::{password}";

    public BuyerPasswordCheck Verify(string? hash, string password) =>
        hash == $"old-hash::{password}" ? BuyerPasswordCheck.SuccessRehashNeeded : BuyerPasswordCheck.Failed;
}

internal sealed class FakeStorefronts : IBuyerStorefronts
{
    public Task<BuyerStorefront> ResolveAsync(string host, CancellationToken cancellationToken) =>
        host == "no-existe.lulocrm.com"
            ? throw new ResourceNotFoundException(BuyerErrorCodes.StoreNotFound, "The store was not found.")
            : Task.FromResult(new BuyerStorefront(host.Trim().ToLowerInvariant(), "Tienda Demo"));
}

internal sealed class FakeCaptcha : IBuyerCaptcha
{
    public bool Passes { get; set; } = true;

    public int Calls { get; private set; }

    public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Passes);
    }
}
