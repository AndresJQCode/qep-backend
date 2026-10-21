using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Pos.Infrastructure.Persistence;
using Npgsql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// La migración <c>AddVariantToSaleLines</c> (spec 2026-10-20 §4 y §8) contra una base con una venta
/// de antes de las variantes. A diferencia de <c>VariantLinesMigrationTests</c> de Quotations, Pos no se
/// puede migrar solo desde cero: <c>InitialPos</c> crea la FK de <c>pos.cash_sessions</c> a
/// <c>companies.companies</c>. Así que el host de pruebas migra todo a la última al arrancar y la prueba
/// baja sólo Pos a <c>AddPosCurrency</c> con <see cref="IMigrator"/> (corre el <c>Down</c> de esta
/// migración), siembra la venta vieja y vuelve a subir.
///
/// El relleno es <c>variant_id = product_id</c> porque la variante por defecto de cada producto migrado
/// nació con el id del producto (Catalog, <c>AddProductVariants</c>): ninguna línea cambia de foto ni de
/// importe.
/// </summary>
public sealed class SaleLinesVariantMigrationTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000f001";
    private const string SessionId = "01900000-0000-7000-8000-00000000f002";
    private const string SaleId = "01900000-0000-7000-8000-00000000f003";
    private const string ProductId = "01900000-0000-7000-8000-00000000f00a";

    // FK apagada: la venta no necesita su caja para la prueba. Los CHECK siguen activos, así que el
    // estado es uno válido.
    private const string SaleSql = $"""
        SET session_replication_role = replica;
        INSERT INTO pos.sales (
            id, request_fingerprint, tenant_id, cash_session_id, cashier_id, sale_number, customer_id, customer_name,
            customer_identification_type, customer_identification_number, subtotal, tax_amount, discount_amount, total,
            change_amount, status, void_reason, voided_at, voided_by, created_at, currency)
        VALUES (
            '{SaleId}', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{TenantId}', '{SessionId}',
            '01900000-0000-7000-8000-00000000f004', 'POS-000001', NULL, 'Consumidor final', NULL, '222222222222',
            10000.00, 1900.00, 0.00, 11900.00, 0.00, 'Completed', NULL, NULL, NULL, '2026-10-07T15:00:00Z', 'COP');
        INSERT INTO pos.sale_lines (
            id, sale_id, position, product_id, product_code, product_name, quantity, unit_price, discount_percentage,
            tax_percentage, discount_amount, tax_amount, subtotal)
        VALUES (
            '01900000-0000-7000-8000-00000000f005', '{SaleId}', 1, '{ProductId}', 'SH-400', 'Shampoo 400 ml',
            1.00, 11900.00, 0.00, 19, 0.00, 1900.00, 10000.00);
        RESET session_replication_role;
        """;

    [Fact]
    public async Task MigrationBackfillsVariantIdFromProductIdAndKeepsTheSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateEverythingAsync(connectionString);
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddPosCurrency"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SaleSql);

        await migrator.MigrateAsync(MigrationId(context, "_AddVariantToSaleLines"), TestContext.Current.CancellationToken);

        const string lines = "FROM pos.sale_lines";
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {lines}"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) {lines} WHERE variant_id = '{ProductId}' AND product_id = '{ProductId}'"));
        // La foto y los importes no se tocan (spec §8).
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) {lines} WHERE product_code = 'SH-400' AND product_name = 'Shampoo 400 ml'"));
        Assert.Equal(10000.00m, await ScalarAsync<decimal>(connectionString, $"SELECT subtotal {lines}"));
        Assert.Equal(11900.00m, await ScalarAsync<decimal>(connectionString, $"SELECT total FROM pos.sales WHERE id = '{SaleId}'"));
        Assert.Equal("NO", await ScalarAsync<string>(
            connectionString,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'pos' " +
            "AND table_name = 'sale_lines' AND column_name = 'variant_id'"));
        // El índice que usa la consulta de uso de Catalog (CatalogVariantUsageChecker).
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'pos' AND indexname = 'IX_sale_lines_variant'"));
    }

    // La parte de Pos de la consulta de uso de Catalog, tal como la escribe CatalogVariantUsageChecker,
    // corre contra el esquema migrado y encuentra la variante rellenada: el acoplamiento de nombres
    // (pos.sale_lines.variant_id, JOIN a pos.sales por sale_id, filtro por tenant_id) queda amarrado acá.
    [Fact]
    public async Task TheCatalogUsageQueryFindsTheBackfilledVariant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateEverythingAsync(connectionString);
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddPosCurrency"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SaleSql);
        await migrator.MigrateAsync(MigrationId(context, "_AddVariantToSaleLines"), TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            $"""
            SELECT count(*) FROM (
                SELECT sl.variant_id FROM pos.sale_lines sl
                JOIN pos.sales s ON s.id = sl.sale_id
                WHERE s.tenant_id = '{TenantId}' AND sl.variant_id = ANY(ARRAY['{ProductId}'::uuid])
            ) used
            """));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            $"""
            SELECT count(*) FROM pos.sale_lines sl
            JOIN pos.sales s ON s.id = sl.sale_id
            WHERE s.tenant_id = '{Guid.CreateVersion7()}' AND sl.variant_id = ANY(ARRAY['{ProductId}'::uuid])
            """));
    }

    /// <summary>Arranca el host una vez: Program migra todos los módulos a la última.</summary>
    private static async Task MigrateEverythingAsync(string connectionString)
    {
        await using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
    }

    private static PosDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "pos"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(PosDbContext context, string suffix) =>
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
