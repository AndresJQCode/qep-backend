using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Storage.Infrastructure.Persistence;
using Npgsql;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Spec 2026-11-26 («Migraciones y despliegue»): <c>AddStorageInboxClaims</c> es aditiva. Las filas que ya existen quedan terminadas, con un
/// intento y sin reclamo; el <c>Down</c> borra sólo las filas reclamadas sin terminar (las de <c>storage.hr-document-release</c>) antes de
/// volver <c>processed_at</c> obligatorio, como <c>AddInboxClaims</c> de Identity. Migra sólo Storage con <see cref="IMigrator"/>, sobre una
/// base vacía.
/// </summary>
public sealed class StorageInboxClaimsMigrationTests
{
    private const string PreviousMigration = "20261115195118_AddFileResourceTenantOwnerCreatedIndex";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpKeepsTheRowsAndDownDropsOnlyUnfinishedOnes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewStorageContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Ct);

        var finished = Guid.CreateVersion7();
        await ExecuteAsync(connectionString, $"""
            INSERT INTO storage.inbox_messages (consumer, message_id, processed_at)
            VALUES ('storage.product-image-release', '{finished}', now());
            """);

        await migrator.MigrateAsync(cancellationToken: Ct);

        Assert.Equal((1, true), await ClaimOfAsync(connectionString, finished));

        var unfinished = Guid.CreateVersion7();
        await ExecuteAsync(connectionString, $"""
            INSERT INTO storage.inbox_messages (consumer, message_id, claimed_until, attempts)
            VALUES ('storage.hr-document-release', '{unfinished}', now() + interval '5 seconds', 1);
            """);

        await migrator.MigrateAsync(PreviousMigration, Ct);

        Assert.Equal([finished], await MessageIdsAsync(connectionString));

        await migrator.MigrateAsync(cancellationToken: Ct);
    }

    private static StorageDbContext NewStorageContext(string connectionString) =>
        new(new DbContextOptionsBuilder<StorageDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "storage"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<(int Attempts, bool ClaimedUntilIsNull)> ClaimOfAsync(string connectionString, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT attempts, claimed_until IS NULL FROM storage.inbox_messages WHERE message_id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    private static async Task<List<Guid>> MessageIdsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT message_id FROM storage.inbox_messages", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
