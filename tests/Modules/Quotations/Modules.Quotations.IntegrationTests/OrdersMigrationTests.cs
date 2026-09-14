using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las migraciones que renombran ventas a pedidos (spec 2026-09-14), contra una base con filas del
/// esquema viejo. Migra sólo Quotations, y hasta una migración puntual, con <see cref="IMigrator"/>:
/// el host de pruebas migra todo a la última al arrancar, y así no habría esquema viejo donde
/// sembrar.
///
/// Las filas viejas se insertan con <c>session_replication_role = replica</c>, que apaga el chequeo
/// de la FK hacia <c>quotations.quotations</c>. Una cotización real en ese estado del esquema exige
/// decenas de columnas obligatorias que no tienen que ver con lo que se prueba, y la FK se verifica
/// igual, por nombre, en <c>pg_constraint</c>. El usuario del contenedor es superusuario, que es lo
/// que ese SET pide.
/// </summary>
public sealed class OrdersMigrationTests
{
    private const string LastMigrationBeforeTheRename = "20260913184747_AddQuotationPartyTaxProfile";

    private const string TenantId = "01900000-0000-7000-8000-00000000c001";
    private const string OrderId = "01900000-0000-7000-8000-00000000c002";
    private const string ProofId = "01900000-0000-7000-8000-00000000c003";
    private const string MemberId = "01900000-0000-7000-8000-00000000c005";

    private const string LegacyRowsSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.sales (
            id, tenant_id, sale_number, quotation_id, status, payment_status, notes, converted_at,
            converted_by, approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'VEN-2026-0001', '01900000-0000-7000-8000-00000000c004', 'Pending',
            'FullPaymentReceived', NULL, '2026-09-10T15:00:00Z', '{MemberId}',
            NULL, NULL, NULL, '2026-09-10T15:00:00Z', '2026-09-10T15:00:00Z', 1);
        INSERT INTO quotations.sale_payment_proofs (id, sale_id, file_id, amount, uploaded_by, uploaded_at)
        VALUES (
            '{ProofId}', '{OrderId}', '01900000-0000-7000-8000-00000000c006', 150000.00,
            '{MemberId}', '2026-09-10T15:00:00Z');
        INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)
        VALUES ('{TenantId}', 2026, 2);
        RESET session_replication_role;
        """;

    private const string TablesSql = """
        SELECT tablename FROM pg_tables
        WHERE schemaname = 'quotations'
          AND tablename IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
        """;

    private const string IndexesSql = """
        SELECT indexname FROM pg_indexes
        WHERE schemaname = 'quotations'
          AND tablename IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
        """;

    // Sólo PK y FK: en PostgreSQL 18 los NOT NULL también son constraints con nombre
    // (<tabla>_<columna>_not_null), EF no los modela y un RENAME de tabla no los renombra.
    private const string ConstraintsSql = """
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'quotations'
          AND t.relname IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
          AND c.contype IN ('p', 'f')
        """;

    [Fact]
    public async Task TheRenameKeepsEveryOrderProofAndCounterUnderTheNewNames()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRowsSql);

        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("VEN-2026-0001", await ScalarAsync<string>(
            connectionString, $"SELECT order_number FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(Guid.Parse(OrderId), await ScalarAsync<Guid>(
            connectionString, $"SELECT order_id FROM quotations.order_payment_proofs WHERE id = '{ProofId}'"));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT next_value FROM quotations.order_number_counters WHERE tenant_id = '{TenantId}' AND year = 2026"));
        Assert.Equal(
            ["order_number_counters", "order_payment_proofs", "orders"],
            await ListAsync(connectionString, TablesSql));
        Assert.Equal(
            ["IX_order_payment_proofs_order", "IX_orders_quotation", "IX_orders_tenant",
             "IX_orders_tenant_converted_at_number", "IX_orders_tenant_number",
             "PK_order_number_counters", "PK_order_payment_proofs", "PK_orders"],
            await ListAsync(connectionString, IndexesSql));
        Assert.Equal(
            ["FK_order_payment_proofs_orders_order_id", "FK_orders_quotations_quotation_id",
             "PK_order_number_counters", "PK_order_payment_proofs", "PK_orders"],
            await ListAsync(connectionString, ConstraintsSql));
    }

    [Fact]
    public async Task RevertingTheRenameBringsBackTheOldNamesWithTheRows()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRowsSql);
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);

        Assert.Equal("VEN-2026-0001", await ScalarAsync<string>(
            connectionString, $"SELECT sale_number FROM quotations.sales WHERE id = '{OrderId}'"));
        Assert.Equal(Guid.Parse(OrderId), await ScalarAsync<Guid>(
            connectionString, $"SELECT sale_id FROM quotations.sale_payment_proofs WHERE id = '{ProofId}'"));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT next_value FROM quotations.sale_number_counters WHERE tenant_id = '{TenantId}' AND year = 2026"));
        Assert.Equal(
            ["sale_number_counters", "sale_payment_proofs", "sales"],
            await ListAsync(connectionString, TablesSql));
        Assert.Equal(
            ["IX_sale_payment_proofs_sale", "IX_sales_quotation", "IX_sales_tenant",
             "IX_sales_tenant_converted_at_number", "IX_sales_tenant_number",
             "PK_sale_number_counters", "PK_sale_payment_proofs", "PK_sales"],
            await ListAsync(connectionString, IndexesSql));
        Assert.Equal(
            ["FK_sale_payment_proofs_sales_sale_id", "FK_sales_quotations_quotation_id",
             "PK_sale_number_counters", "PK_sale_payment_proofs", "PK_sales"],
            await ListAsync(connectionString, ConstraintsSql));
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

    /// <summary>Ordenado en C# y no con ORDER BY: el orden de texto de Postgres depende de la
    /// collation de la base.</summary>
    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }
}
