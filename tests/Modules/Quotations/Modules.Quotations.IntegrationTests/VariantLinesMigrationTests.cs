using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración <c>AddVariantToLines</c> (spec 2026-10-20 §4 y §8) contra una base con una cotización y
/// su pedido de antes de las variantes. Migra sólo Quotations y hasta una migración puntual con
/// <see cref="IMigrator"/>, como <see cref="FreightMigrationTests"/>: el host de pruebas migra todo a la
/// última al arrancar y así no habría esquema anterior donde sembrar.
///
/// El relleno es <c>variant_id = product_id</c> porque la variante por defecto de cada producto migrado
/// nació con el id del producto (Catalog, <c>AddProductVariants</c>): ninguna línea cambia de foto ni de
/// total.
/// </summary>
public sealed class VariantLinesMigrationTests
{
    private const string LastMigrationBeforeTheOrderOwnData = "20261009045958_AddQuotationSettings";

    private const string TenantId = "01900000-0000-7000-8000-00000000e001";
    private const string OrderId = "01900000-0000-7000-8000-00000000e002";
    private const string QuotationId = "01900000-0000-7000-8000-00000000e004";
    private const string MemberId = "01900000-0000-7000-8000-00000000e005";
    private const string ProductId = "01900000-0000-7000-8000-00000000e00a";

    // Misma siembra que FreightMigrationTests: FK apagada, cotización enviada con su línea congelada, y un
    // pedido que AddOrderOwnData copia desde ella (líneas incluidas).
    private const string QuotationAndOrderSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number,
            billing_account_currency)
        VALUES (
            '{QuotationId}', '{TenantId}', 'COT-2026-0008', '01900000-0000-7000-8000-00000000e007',
            '{MemberId}', 'Sent', '2026-09-09T15:00:00Z', '2026-10-09', 'COP', 'Transferencia',
            100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, true, false, 2500.00, 116500.00,
            NULL, '{MemberId}', '{MemberId}', '2026-09-09T15:00:00Z', 2, '01900000-0000-7000-8000-00000000e008',
            'Bancolombia', '7542', 'COP');
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, product_code, product_name, quantity, unit_price, discount_origin, discount_percentage,
            discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        VALUES (
            '01900000-0000-7000-8000-00000000e009', '{QuotationId}', '{ProductId}',
            'ABC', 'Aceite', 10.00, 11900.00, 'Own', 0.00, 0.00, 100000.00, 19, 19000.00, 1, '2026-09-09T15:00:00Z', '2026-09-09T15:00:00Z');
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'PED-2026-0002', '{QuotationId}', 'Pending', 'PaymentPending',
            NULL, '2026-09-10T15:00:00Z', '{MemberId}', NULL, NULL, NULL, '2026-09-10T15:00:00Z',
            '2026-09-10T15:00:00Z', 1);
        RESET session_replication_role;
        """;

    [Fact]
    public async Task MigrationBackfillsVariantIdFromProductIdAndKeepsTotals()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheOrderOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationAndOrderSql);
        // Hasta la migración anterior a la de esta prueba: el pedido gana sus líneas propias y el flete.
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderExternalKey"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(MigrationId(context, "_AddVariantToLines"), TestContext.Current.CancellationToken);

        foreach (var table in new[] { "quotation_items", "order_items" })
        {
            var lines = $"FROM quotations.{table}";
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {lines}"));
            Assert.Equal(1L, await ScalarAsync<long>(
                connectionString, $"SELECT count(*) {lines} WHERE variant_id = '{ProductId}' AND product_id = '{ProductId}'"));
            // La foto no se toca (spec §8).
            Assert.Equal(1L, await ScalarAsync<long>(
                connectionString, $"SELECT count(*) {lines} WHERE product_code = 'ABC' AND product_name = 'Aceite'"));
            Assert.Equal(100000.00m, await ScalarAsync<decimal>(connectionString, $"SELECT subtotal {lines}"));
            Assert.Equal("NO", await ScalarAsync<string>(
                connectionString,
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'quotations' " +
                $"AND table_name = '{table}' AND column_name = 'variant_id'"));
        }

        foreach (var (table, id) in new[] { ("quotations", QuotationId), ("orders", OrderId) })
        {
            var where = $"FROM quotations.{table} WHERE id = '{id}'";
            Assert.Equal(119000.00m, await ScalarAsync<decimal>(connectionString, $"SELECT total {where}"));
            Assert.Equal(116500.00m, await ScalarAsync<decimal>(connectionString, $"SELECT net_total {where}"));
        }

        // Los dos índices que usa la consulta de uso de Catalog (CatalogVariantUsageChecker).
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'quotations' " +
            "AND indexname IN ('IX_quotation_items_variant', 'IX_order_items_variant')"));
    }

    // El CatalogVariantUsageChecker real, resuelto del contenedor de la app, contra el esquema migrado:
    // encuentra la variante rellenada en las líneas de la cotización y del pedido, y no la ve desde otro
    // tenant. Es el adaptador mismo y no una copia de su SQL, así que el acoplamiento de nombres entre los
    // módulos (variant_id, el JOIN al documento dueño, el filtro por tenant_id) queda amarrado acá: si
    // Quotations renombra la columna, esto sale 42703 en vez de un 500 en el PUT de producto.
    [Fact]
    public async Task TheCatalogUsageQueryFindsTheBackfilledVariant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(LastMigrationBeforeTheOrderOwnData, TestContext.Current.CancellationToken);
            await ExecuteAsync(connectionString, QuotationAndOrderSql);
            await migrator.MigrateAsync(MigrationId(context, "_AddVariantToLines"), TestContext.Current.CancellationToken);
        }

        // El host migra al arrancar los módulos que faltan: la consulta del adaptador también lee
        // pos.sale_lines y corre sobre el CatalogDbContext. Quotations ya quedó en AddVariantToLines con la
        // cotización y el pedido sembrados.
        await using var factory = new QepApiFactory(connectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<IVariantUsageChecker>();
        var variantId = Guid.Parse(ProductId);

        var used = await checker.FindUsedAsync(
            Guid.Parse(TenantId), [variantId], TestContext.Current.CancellationToken);
        var fromAnotherTenant = await checker.FindUsedAsync(
            Guid.CreateVersion7(), [variantId], TestContext.Current.CancellationToken);

        Assert.Equal(variantId, Assert.Single(used));
        Assert.Empty(fromAnotherTenant);
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
