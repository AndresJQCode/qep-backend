using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task APasswordCredentialRoundTripsAndIsDeletedWithItsUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var migrating = NewIdentityContext(connectionString))
        {
            await migrating.Database.MigrateAsync(cancellationToken);
        }

        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var user = User.CreateInvited(UserId.New(), "person@example.com", now);
        await using (var writing = NewIdentityContext(connectionString))
        {
            new UserRepository(writing).Add(user);
            new PasswordCredentialRepository(writing).Add(PasswordCredential.Create(user.Id, "stored-hash", now));
            await writing.SaveChangesAsync(cancellationToken);
        }

        await using (var reading = NewIdentityContext(connectionString))
        {
            var credential = await new PasswordCredentialRepository(reading)
                .FindByUserAsync(user.Id.Value, cancellationToken);
            Assert.Equal("stored-hash", credential?.Hash);
        }

        await using (var deleting = NewIdentityContext(connectionString))
        {
            var repository = new UserRepository(deleting);
            var stored = await repository.FindByIdAsync(user.Id.Value, cancellationToken);
            repository.Remove(stored!);
            await deleting.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal(0, await CountRowsAsync(connectionString, "identity.password_credentials", user.Id.Value));
    }

    private static readonly DateTimeOffset TokenNow = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    // Siembra un usuario con un token Verify cuyo crudo es "raw-1".
    private static async Task<Guid> SeedUserWithVerifyTokenAsync(string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var migrating = NewIdentityContext(connectionString))
        {
            await migrating.Database.MigrateAsync(cancellationToken);
        }

        var user = User.CreateInvited(UserId.New(), "person@example.com", TokenNow);
        await using var writing = NewIdentityContext(connectionString);
        new UserRepository(writing).Add(user);
        new EmailTokenRepository(writing).Add(EmailToken.Issue(
            EmailTokenId.New(),
            user.Id,
            EmailTokenPurpose.Verify,
            EmailTokenCodec.Hash("raw-1"),
            TokenNow));
        await writing.SaveChangesAsync(cancellationToken);
        return user.Id.Value;
    }

    [Fact]
    public async Task TryConsumeSucceedsOnceThenReturnsNull()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserWithVerifyTokenAsync(cs);

        var first = await NewTokenRepository(cs).TryConsumeAsync(
            EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, TokenNow.AddMinutes(1), ct);
        var second = await NewTokenRepository(cs).TryConsumeAsync(
            EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, TokenNow.AddMinutes(2), ct);

        Assert.Equal(userId, first);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryConsumeRejectsAnExpiredTokenAndTheWrongPurpose()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        await SeedUserWithVerifyTokenAsync(cs);

        Assert.Null(await NewTokenRepository(cs).TryConsumeAsync(
            EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Reset, TokenNow.AddMinutes(1), ct));
        Assert.Null(await NewTokenRepository(cs).TryConsumeAsync(
            EmailTokenCodec.Hash("raw-1"), EmailTokenPurpose.Verify, TokenNow.AddHours(24), ct));
    }

    [Fact]
    public async Task InvalidateUnusedOnlyTouchesTheGivenPurposeUnlessItIsNull()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserWithVerifyTokenAsync(cs);
        var hash = EmailTokenCodec.Hash("raw-1");

        await InvalidateAsync(cs, userId, EmailTokenPurpose.Reset);
        await using (var probing = NewIdentityContext(cs))
        {
            Assert.False(probing.Database.SqlQuery<bool>(
                $"SELECT used_at IS NOT NULL AS \"Value\" FROM identity.email_tokens WHERE token_hash = {hash}")
                .ToList().Single());
        }

        await InvalidateAsync(cs, userId, null);
        Assert.Null(await NewTokenRepository(cs).TryConsumeAsync(
            hash, EmailTokenPurpose.Verify, TokenNow.AddMinutes(1), ct));
    }

    private static async Task InvalidateAsync(string cs, Guid userId, EmailTokenPurpose? purpose)
    {
        await using var invalidating = NewIdentityContext(cs);
        await new EmailTokenRepository(invalidating).InvalidateUnusedAsync(
            userId, purpose, TokenNow, TestContext.Current.CancellationToken);
        await invalidating.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnIdentityOutboxRowIsWrittenWithTheUnitOfWorkAndProcessedByThePlatform()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new PasswordAuthApiFactory(cs);
        // Fuerza el arranque del host, que migra la base.
        _ = factory.Services;

        var user = User.CreateInvited(UserId.New(), "outbox@example.com", DateTimeOffset.UtcNow);
        await using (var writing = NewIdentityContext(cs))
        {
            new UserRepository(writing).Add(user);
            await writing.SaveChangesAsync(cancellationToken);
        }

        var userId = user.Id.Value;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IIdentityOutbox>().Add(
                IdentityIntegrationEvents.ExistingAccountNotice,
                new ExistingAccountNoticePayload(userId),
                "corr",
                DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>()
                .SaveChangesAsync(cancellationToken);
        }

        var row = await ReadOutboxRowAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice);
        Assert.Equal($$"""{"userId": "{{userId}}"}""", row!.PayloadText);
        Assert.Equal("corr", row.CorrelationId);
        // OutboxProcessor de Tenancy: sin handlers, lo marca procesado.
        await WaitUntilAsync(async () =>
            (await ReadOutboxRowAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice))!.ProcessedAt is not null);
    }
}
