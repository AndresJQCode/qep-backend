using BuildingBlocks.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración de la regla de redondeo del documento (spec 2026-10-27 §4), contra una cotización en
/// dólares y un pedido en pesos guardados antes de que existieran las columnas. Migra sólo Quotations
/// y hasta una migración puntual con <see cref="IMigrator"/>, como <see cref="FreightMigrationTests"/>:
/// el host de pruebas migra todo a la última al arrancar y así no habría esquema anterior donde
/// sembrar. Sin relleno, el documento viejo se lee con la regla de siempre de su moneda.
/// </summary>
public sealed class DocumentPriceRoundingMigrationTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000f001";
    private const string UsdQuotationId = "01900000-0000-7000-8000-00000000f002";
    private const string CopQuotationId = "01900000-0000-7000-8000-00000000f003";
    private const string OrderId = "01900000-0000-7000-8000-00000000f004";
    private const string MemberId = "01900000-0000-7000-8000-00000000f005";

    // FK apagada, como en FreightMigrationTests: cotizaciones mínimas, sin líneas, y un pedido en COP.
    private const string DocumentsSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number,
            billing_account_currency)
        VALUES
            ('{UsdQuotationId}', '{TenantId}', 'COT-2026-0101', '01900000-0000-7000-8000-00000000f007',
             '{MemberId}', 'Sent', '2026-10-20T15:00:00Z', '2026-11-20', 'USD', 'Transferencia',
             0.00, 0.00, 0.00, 0.00, 0.00, false, false, false, false, false, false, 0.00, 0.00,
             NULL, '{MemberId}', '{MemberId}', '2026-10-20T15:00:00Z', 2, '01900000-0000-7000-8000-00000000f008',
             'Bancolombia', '7542', 'USD'),
            ('{CopQuotationId}', '{TenantId}', 'COT-2026-0102', '01900000-0000-7000-8000-00000000f007',
             '{MemberId}', 'Sent', '2026-10-20T15:00:00Z', '2026-11-20', 'COP', 'Transferencia',
             0.00, 0.00, 0.00, 0.00, 0.00, false, false, false, false, false, false, 0.00, 0.00,
             NULL, '{MemberId}', '{MemberId}', '2026-10-20T15:00:00Z', 2, '01900000-0000-7000-8000-00000000f008',
             'Bancolombia', '7542', 'COP');
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version, client_id, advisor_id,
            currency, payment_method, subtotal, tax_percentage, tax_amount, discount_amount, total,
            billing_company_id, billing_bank_name, billing_account_number, billing_account_currency)
        VALUES (
            '{OrderId}', '{TenantId}', 'PED-2026-0101', '{CopQuotationId}', 'Pending', 'PaymentPending',
            NULL, '2026-10-21T15:00:00Z', '{MemberId}', NULL, NULL, NULL, '2026-10-21T15:00:00Z',
            '2026-10-21T15:00:00Z', 1, '01900000-0000-7000-8000-00000000f007', '{MemberId}',
            'COP', 'Transferencia', 0.00, 0.00, 0.00, 0.00, 0.00,
            '01900000-0000-7000-8000-00000000f008', 'Bancolombia', '7542', 'COP');
        RESET session_replication_role;
        """;

    [Fact]
    public async Task ExistingDocumentsReadTheDefaultRule()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith("_AddDocumentPriceRounding", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(target) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, DocumentsSql);
        // Hasta la última antes de leer: el modelo de hoy pide las columnas de esta migración
        // (CLAUDE.md, gotcha de las pruebas de migración).
        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM (" +
            "SELECT price_rounding_decimals AS d, price_rounding_mode AS m FROM quotations.quotations " +
            "UNION ALL SELECT price_rounding_decimals, price_rounding_mode FROM quotations.orders) AS documents " +
            "WHERE d IS NULL AND m IS NULL"));

        var usd = await context.Set<Quotation>().SingleAsync(
            quotation => quotation.Currency == "USD", TestContext.Current.CancellationToken);
        var cop = await context.Set<Order>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new PriceRounding(2, PriceRoundingMode.Nearest), usd.PriceRounding);
        Assert.Equal(new PriceRounding(0, PriceRoundingMode.Nearest), cop.PriceRounding);
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

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
