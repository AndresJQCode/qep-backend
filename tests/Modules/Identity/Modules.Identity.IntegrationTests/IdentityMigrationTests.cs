using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Migraciones de Identity que llevan datos. Migra sólo Identity con <see cref="IMigrator"/>,
/// mismo patrón que <c>TenantModulesMigrationTests</c>.
/// </summary>
public sealed class IdentityMigrationTests
{
    private const string PreviousMigration = "20261015083738_AddMessageSoundPreference";

    [Fact]
    public async Task EmailVerifiedAtIsBackfilledFromTheFirstProviderLink()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewIdentityContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var t1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(30);
        await ExecuteAsync(connectionString, $"""
            INSERT INTO identity.users (id, email, status, created_at, updated_at) VALUES
                ('{userA}', 'a@example.com', 'Active', now(), now()),
                ('{userB}', 'b@example.com', 'Invited', now(), now());
            INSERT INTO identity.provider_links (id, user_id, provider, subject, linked_at) VALUES
                (gen_random_uuid(), '{userA}', 'google', 'sub-a1', '{t2:O}'),
                (gen_random_uuid(), '{userA}', 'microsoft', 'sub-a2', '{t1:O}');
            """);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(t1, await EmailVerifiedAtAsync(connectionString, userA));
        Assert.Null(await EmailVerifiedAtAsync(connectionString, userB));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
