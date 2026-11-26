using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Pos.Infrastructure.Persistence;
using Npgsql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// La migración <c>AddCashSessionCompanyChargesVat</c> (spec 2026-11-17 R4): una caja abierta antes de la columna sigue
/// cobrando IVA, que es como vendía; y una caja de empresa sin IVA guarda <c>false</c> (sin el sentinel, EF omitiría el
/// <c>false</c> del INSERT y la base pondría <c>true</c>). Mismo esquema que <see cref="CashSessionDocumentTypeMigrationTests"/>.
/// </summary>
public sealed class CashSessionChargesVatMigrationTests
{
    private const string SessionSql = """
        SET session_replication_role = replica;
        INSERT INTO pos.cash_sessions (
            id, tenant_id, cashier_id, cashier_name, company_id, company_name, company_tax_id, company_document_type,
            company_address, company_phone, status, opening_float, opened_at, sales_count, voided_count, sales_total,
            cash_total, card_total, transfer_total, version, created_at, updated_at, currency)
        VALUES (
            '01900000-0000-7000-8000-00000000f002', '01900000-0000-7000-8000-00000000f001',
            '01900000-0000-7000-8000-00000000f003', 'Laura', '01900000-0000-7000-8000-00000000f004', 'Empresa',
            '900123456-1', 'NIT', NULL, NULL, 'Open', 0, '2026-11-16T15:00:00Z', 0, 0, 0, 0, 0, 0, 1,
            '2026-11-16T15:00:00Z', '2026-11-16T15:00:00Z', 'COP');
        RESET session_replication_role;
        """;

    [Fact]
    public async Task AnOpenCashSessionBeforeTheMigrationKeepsVat()
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
            .Single(id => id.EndsWith("_AddSaleLineCost", StringComparison.Ordinal));
        await migrator.MigrateAsync(before, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SessionSql);

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.True(await ScalarAsync<bool>(connectionString, "SELECT company_charges_vat FROM pos.cash_sessions"));
    }

    [Fact]
    public async Task ACashSessionWithoutVatKeepsItAfterReload()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database, chargesVat: false);

        var session = await world.OpenSessionAsync(world.Admin);

        Assert.False(await ScalarAsync<bool>(
            database.GetConnectionString(), $"SELECT company_charges_vat FROM pos.cash_sessions WHERE id = '{session.Id}'"));
        await using var context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options);
        var reloaded = await context.Set<Modules.Pos.Domain.CashSession>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.False(reloaded.CompanyChargesVat);
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
