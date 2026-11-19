using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §4: el consumo del código es una sola sentencia atómica, fuera de la unidad de
/// trabajo, como <see cref="EmailTokenRepository.TryConsumeAsync"/>. Cada consumo va en su propio contexto, como en
/// producción.
/// </summary>
public sealed class EmailChallengeRepositoryTests
{
    private const string Email = "person@example.com";
    private const string Code = "123456";

    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AConsumedCodeCannotBeUsedAgain()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await SeedChallengeAsync(cs, Email, Code);

        Assert.True(await ConsumeAsync(cs, Email, Code, IssuedAt.AddMinutes(1)));
        Assert.False(await ConsumeAsync(cs, Email, Code, IssuedAt.AddMinutes(2)));
    }

    [Fact]
    public async Task AnExpiredCodeIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await SeedChallengeAsync(cs, Email, Code);

        Assert.False(await ConsumeAsync(cs, Email, Code, IssuedAt + EmailChallengeService.Lifetime));
    }

    [Fact]
    public async Task TheFifthWrongAttemptInvalidatesTheChallengeEvenIfTheNextCodeIsRight()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await SeedChallengeAsync(cs, Email, Code);

        for (var attempt = 1; attempt < EmailChallengeService.MaxAttempts; attempt++)
        {
            Assert.False(await ConsumeAsync(cs, Email, "000000", IssuedAt.AddMinutes(1)));
        }

        // Con cuatro errores el desafío sigue vivo: sólo el quinto lo invalida.
        Assert.Equal((EmailChallengeService.MaxAttempts - 1, false), await ReadStateAsync(cs));

        Assert.False(await ConsumeAsync(cs, Email, "000000", IssuedAt.AddMinutes(1)));
        Assert.Equal((EmailChallengeService.MaxAttempts, true), await ReadStateAsync(cs));

        Assert.False(await ConsumeAsync(cs, Email, Code, IssuedAt.AddMinutes(2)));
    }

    [Fact]
    public async Task AnotherEmailsCodeIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await SeedChallengeAsync(cs, Email, Code);

        Assert.False(await ConsumeAsync(cs, "other@example.com", Code, IssuedAt.AddMinutes(1)));
        // Un correo sin desafío no le cuenta intentos a otro.
        Assert.Equal((0, false), await ReadStateAsync(cs));
        Assert.True(await ConsumeAsync(cs, Email, Code, IssuedAt.AddMinutes(1)));
    }

    // Revisión final de la rama, hallazgo 1: con varios desafíos vivos del mismo correo, cada intento probaba todos los códigos a
    // la vez. Sólo cuenta el más reciente; el código de uno anterior es un código errado.
    [Fact]
    public async Task OnlyTheNewestChallengeCountsAndAnOlderCodeIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await SeedChallengeAsync(cs, Email, "111111", IssuedAt);
        await SeedChallengeAsync(cs, Email, Code, IssuedAt.AddSeconds(1), migrate: false);

        Assert.False(await ConsumeAsync(cs, Email, "111111", IssuedAt.AddMinutes(1)));
        Assert.True(await ConsumeAsync(cs, Email, Code, IssuedAt.AddMinutes(1)));

        // El viejo sigue sin usar y aun así no sirve: el más reciente ya se consumió.
        Assert.False(await ConsumeAsync(cs, Email, "111111", IssuedAt.AddMinutes(2)));
    }

    // Revisión final de la rama, hallazgo 1: la comprobación de los 60 s era leer-y-luego-insertar, así que pedidos simultáneos
    // dejaban varios desafíos vivos. Bajo el candado por correo queda uno solo.
    [Fact]
    public async Task ConcurrentIssuesLeaveOneLiveChallenge()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        await using (var migrating = NewIdentityContext(cs))
        {
            await migrating.Database.MigrateAsync(ct);
        }

        const int Parallel = 8;
        using var start = new Barrier(Parallel);
        var issues = Enumerable.Range(0, Parallel).Select(_ => Task.Run(
            async () =>
            {
                await using var context = NewIdentityContext(cs);
                var service = new EmailChallengeService(
                    new EmailChallengeRepository(context),
                    new DiscardingOutbox(),
                    new IdentityUnitOfWork(context),
                    new FixedClock(IssuedAt));
                start.SignalAndWait(ct);
                await service.IssueAsync(Email, ct);
            },
            ct));
        await Task.WhenAll(issues);

        await using var reading = NewIdentityContext(cs);
        var rows = await reading.EmailChallenges.AsNoTracking().ToListAsync(ct);
        var live = Assert.Single(rows);
        Assert.Null(live.UsedAt);
    }

    private static Task SeedChallengeAsync(string connectionString, string email, string code) =>
        SeedChallengeAsync(connectionString, email, code, IssuedAt);

    private static async Task SeedChallengeAsync(
        string connectionString, string email, string code, DateTimeOffset issuedAt, bool migrate = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (migrate)
        {
            await using var migrating = NewIdentityContext(connectionString);
            await migrating.Database.MigrateAsync(cancellationToken);
        }

        await using var writing = NewIdentityContext(connectionString);
        new EmailChallengeRepository(writing).Add(EmailChallenge.Issue(
            EmailChallengeId.New(),
            EmailTokenCodec.Hash(email),
            EmailTokenCodec.Hash(code),
            issuedAt,
            issuedAt + EmailChallengeService.Lifetime));
        await writing.SaveChangesAsync(cancellationToken);
    }

    // platform.outbox_messages es de Tenancy y esta base sólo tiene las migraciones de Identity: el evento no es lo que se prueba.
    private sealed class DiscardingOutbox : IIdentityOutbox
    {
        public void Add(string eventName, object payload, string correlationId, DateTimeOffset occurredAt)
        {
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : BuildingBlocks.Application.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static async Task<bool> ConsumeAsync(string connectionString, string email, string code, DateTimeOffset now)
    {
        await using var context = NewIdentityContext(connectionString);
        return await new EmailChallengeRepository(context).TryConsumeAsync(
            EmailTokenCodec.Hash(email),
            EmailTokenCodec.Hash(code),
            EmailChallengeService.MaxAttempts,
            now,
            TestContext.Current.CancellationToken);
    }

    private static async Task<(int Attempts, bool Used)> ReadStateAsync(string connectionString)
    {
        await using var context = NewIdentityContext(connectionString);
        var challenge = await context.EmailChallenges.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        return (challenge.Attempts, challenge.UsedAt is not null);
    }
}
