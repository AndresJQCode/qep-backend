using Microsoft.EntityFrameworkCore;
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
}
