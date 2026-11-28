using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El índice <c>(correlation_id, occurred_at)</c> de <c>platform.outbox_messages</c> que usa el consumidor de inventario básico del WMS
/// para ordenar los mensajes de un mismo documento (decisión del owner, 2026-11-27). Es una migración normal: las escrituras del outbox
/// esperan mientras se construye durante el despliegue.
/// </summary>
public sealed class OutboxCorrelationIndexMigrationTests
{
    private const string IndexName = "IX_outbox_messages_correlation_occurred";

    [Fact]
    public async Task TheMigrationsCreateTheCorrelationIndexOnTheOutbox()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"))
                .Options);

        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'platform' AND tablename = 'outbox_messages' AND indexname = @name",
            connection);
        command.Parameters.AddWithValue("name", IndexName);
        var definition = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;

        Assert.NotNull(definition);
        Assert.Contains("(correlation_id, occurred_at)", definition, StringComparison.Ordinal);
    }
}
