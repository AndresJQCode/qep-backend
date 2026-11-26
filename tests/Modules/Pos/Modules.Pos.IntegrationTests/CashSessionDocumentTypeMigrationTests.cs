using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Infrastructure.Persistence;
using Npgsql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// La migración <c>AddCashSessionCompanyDocumentType</c> contra una caja de antes del tipo de documento:
/// toda caja vieja era de una empresa colombiana, así que el relleno es <c>NIT</c>. Mismo esquema de la
/// prueba de variantes: el host migra todo a la última y la prueba baja sólo Pos a la migración anterior.
/// </summary>
public sealed class CashSessionDocumentTypeMigrationTests
{
    private const string SessionSql = """
        SET session_replication_role = replica;
        INSERT INTO pos.cash_sessions (
            id, tenant_id, cashier_id, cashier_name, company_id, company_name, company_tax_id, company_address,
            company_phone, status, opening_float, opened_at, sales_count, voided_count, sales_total, cash_total,
            card_total, transfer_total, version, created_at, updated_at, currency)
        VALUES (
            '01900000-0000-7000-8000-00000000e002', '01900000-0000-7000-8000-00000000e001',
            '01900000-0000-7000-8000-00000000e003', 'Laura', '01900000-0000-7000-8000-00000000e004', 'Empresa',
            '900123456-1', NULL, NULL, 'Open', 0, '2026-10-07T15:00:00Z', 0, 0, 0, 0, 0, 0, 1,
            '2026-10-07T15:00:00Z', '2026-10-07T15:00:00Z', 'COP');
        RESET session_replication_role;
        """;

    [Fact]
    public async Task MigrationBackfillsNitAndDropsTheDefault()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var factory = new QepApiFactory(connectionString))
        {
            _ = factory.Services;
        }

        await using var context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "pos"))
            .Options);
        var migrator = context.GetService<IMigrator>();
        var before = context.Database.GetMigrations()
            .Single(id => id.EndsWith("_AddSalePriceRounding", StringComparison.Ordinal));
        await migrator.MigrateAsync(before, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SessionSql);

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("NIT", await ScalarAsync<string>(
            connectionString, "SELECT company_document_type FROM pos.cash_sessions"));
        Assert.Null(await ScalarAsync<string?>(
            connectionString,
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'pos' " +
            "AND table_name = 'cash_sessions' AND column_name = 'company_document_type'"));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }
}
