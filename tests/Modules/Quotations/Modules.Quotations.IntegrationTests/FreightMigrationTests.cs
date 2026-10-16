using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración del flete (spec 2026-10-17), contra una base con una cotización y un pedido
/// anteriores. Migra sólo Quotations y hasta una migración puntual con <see cref="IMigrator"/>, como
/// <see cref="OrdersMigrationTests"/>: el host de pruebas migra todo a la última al arrancar y así no
/// habría esquema anterior donde sembrar. Los documentos existentes no pueden cambiar de total.
/// </summary>
public sealed class FreightMigrationTests
{
    private const string LastMigrationBeforeTheOrderOwnData = "20261009045958_AddQuotationSettings";

    private const string TenantId = "01900000-0000-7000-8000-00000000d001";
    private const string OrderId = "01900000-0000-7000-8000-00000000d002";
    private const string QuotationId = "01900000-0000-7000-8000-00000000d004";
    private const string MemberId = "01900000-0000-7000-8000-00000000d005";

    // Misma siembra que OrdersMigrationTests.QuotationWithLinesSql: FK apagada, cotización mínima
    // con su línea, y un pedido que AddOrderOwnData copia desde ella.
    private const string QuotationAndOrderSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number,
            billing_account_currency)
        VALUES (
            '{QuotationId}', '{TenantId}', 'COT-2026-0007', '01900000-0000-7000-8000-00000000d007',
            '{MemberId}', 'Sent', '2026-09-09T15:00:00Z', '2026-10-09', 'COP', 'Transferencia',
            100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, true, false, 2500.00, 116500.00,
            NULL, '{MemberId}', '{MemberId}', '2026-09-09T15:00:00Z', 2, '01900000-0000-7000-8000-00000000d008',
            'Bancolombia', '7542', 'COP');
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, product_code, product_name, quantity, unit_price, discount_origin, discount_percentage,
            discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        VALUES (
            '01900000-0000-7000-8000-00000000d009', '{QuotationId}', '01900000-0000-7000-8000-00000000d00a',
            'ABC', 'Aceite', 10.00, 11900.00, 'Own', 0.00, 0.00, 100000.00, 19, 19000.00, 1, '2026-09-09T15:00:00Z', '2026-09-09T15:00:00Z');
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'PED-2026-0001', '{QuotationId}', 'Pending', 'PaymentPending',
            NULL, '2026-09-10T15:00:00Z', '{MemberId}', NULL, NULL, NULL, '2026-09-10T15:00:00Z',
            '2026-09-10T15:00:00Z', 1);
        RESET session_replication_role;
        """;

    [Fact]
    public async Task ExistingDocumentsKeepTheirTotals()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheOrderOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationAndOrderSql);
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderOwnData"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(MigrationId(context, "_AddFreight"), TestContext.Current.CancellationToken);

        foreach (var (table, id) in new[] { ("quotations", QuotationId), ("orders", OrderId) })
        {
            var where = $"FROM quotations.{table} WHERE id = '{id}'";
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {where} AND carrier_id IS NULL"));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {where} AND carrier_name IS NULL"));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {where} AND freight_charge_mode IS NULL"));
            Assert.Equal(0m, await ScalarAsync<decimal>(connectionString, $"SELECT freight_amount {where}"));
            Assert.Equal(0, await ScalarAsync<int>(connectionString, $"SELECT freight_tax_percentage {where}"));
            Assert.Equal(0m, await ScalarAsync<decimal>(connectionString, $"SELECT freight_subtotal {where}"));
            Assert.Equal(0m, await ScalarAsync<decimal>(connectionString, $"SELECT freight_tax_amount {where}"));
            Assert.False(await ScalarAsync<bool>(connectionString, $"SELECT cash_on_delivery {where}"));
            Assert.Equal(119000.00m, await ScalarAsync<decimal>(connectionString, $"SELECT total {where}"));
            Assert.Equal(116500.00m, await ScalarAsync<decimal>(connectionString, $"SELECT net_total {where}"));
        }
    }

    [Fact]
    public async Task TheMigrationCreatesTheCarrierTablesAndTheSettingsColumn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);

        await context.GetService<IMigrator>().MigrateAsync(
            MigrationId(context, "_AddFreight"), TestContext.Current.CancellationToken);

        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'quotations' AND tablename IN ('carriers', 'carrier_names')"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'quotations' " +
            "AND table_name = 'tenant_quotation_settings' AND column_name = 'freight_tax_rate_id' AND is_nullable = 'YES'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'quotations' AND indexname = 'IX_carrier_names_tenant_name' " +
            "AND indexdef LIKE 'CREATE UNIQUE INDEX%'"));
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(QuotationsDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

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
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
