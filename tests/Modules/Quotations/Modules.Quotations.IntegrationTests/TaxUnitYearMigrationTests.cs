using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El relleno de <c>tax_unit_year</c> (spec retenciones por UVT §8.2, owner P2): cada fila con UVT congelada toma el mayor
/// año cargado con ese país y ese valor que no pase del año del documento; lo que no case queda null. En una base sin
/// Catalog la migración no hace nada (guard de <c>to_regclass</c>, como <c>MigrateRetentionToWithholdings</c>).
///
/// Como <see cref="RetentionToWithholdingsMigrationTests"/>: el host ya migró todo en la plantilla; Quotations baja a la
/// migración anterior, se siembra por SQL y vuelve a subir.
/// </summary>
public sealed class TaxUnitYearMigrationTests
{
    private const string LastMigrationBeforeTheYear = "_AddOrderBuyerAccountSetByMember";
    private const string TenantId = "01900000-0000-7000-8000-0000000f0a01";
    private const string MemberId = "01900000-0000-7000-8000-0000000f0f01";
    private const string Quotation2026 = "01900000-0000-7000-8000-0000000f0201";
    private const string Order2026 = "01900000-0000-7000-8000-0000000f0401";
    private const string Order2025 = "01900000-0000-7000-8000-0000000f0402";

    [Fact]
    public async Task MigratesADatabaseWithoutCatalog()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();

        await using (var context = NewContext(connectionString))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'quotations' "
            + "AND table_name IN ('quotation_withholdings', 'order_withholdings') AND column_name = 'tax_unit_year'"));
    }

    [Fact]
    public async Task BackfillsTheYearByCountryAndValue()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();

        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, LastMigrationBeforeTheYear), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, $"""
            SET session_replication_role = replica;
            DELETE FROM catalog.tax_unit_values WHERE country = 'CO';
            INSERT INTO catalog.tax_unit_values (country, year, value, updated_at, updated_by) VALUES
                ('CO', 2025, 49799.00, now(), '{MemberId}'),
                ('CO', 2026, 52000.00, now(), '{MemberId}');
            {Quotation(Quotation2026, "2026-11-01T15:00:00Z")}
            {Withholding("quotation_withholdings", Quotation2026, "Customer", 0, 49799.00m)}
            {Withholding("quotation_withholdings", Quotation2026, "Customer", 1, 12345.00m)}
            {Withholding("quotation_withholdings", Quotation2026, "Billing", 0, null)}
            {Order(Order2026, "2026-11-02T15:00:00Z")}
            {Withholding("order_withholdings", Order2026, "Document", 0, 52000.00m)}
            {Order(Order2025, "2025-11-02T15:00:00Z")}
            {Withholding("order_withholdings", Order2025, "Document", 0, 52000.00m)}
            RESET session_replication_role;
            """);

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        // La de 49.799 en un documento de 2026 es la UVT de 2025; un valor que no está en la tabla y una fila sin UVT
        // quedan null.
        Assert.Equal(
            new[] { $"{Quotation2026}|Billing|0|", $"{Quotation2026}|Customer|0|2025", $"{Quotation2026}|Customer|1|" },
            await YearsAsync(connectionString, "quotation_withholdings"));
        // El mismo valor de 2026 en un pedido de 2025 no casa: el año no puede pasar del del documento.
        Assert.Equal(
            new[] { $"{Order2026}|Document|0|2026", $"{Order2025}|Document|0|" },
            await YearsAsync(connectionString, "order_withholdings"));
    }

    private static string Quotation(string id, string createdAt) => $"""
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, party_with_retention, customer_vat_surplus, retention_amount,
            net_total, notes, created_by, updated_by, updated_at, version)
        VALUES (
            '{id}', '{TenantId}', 'COT-{id[^5..]}', '{MemberId}', '{MemberId}', 'Draft', '{createdAt}', '2026-12-01', 'COP',
            'Transferencia', 100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, false, NULL, false, 0.00,
            119000.00, NULL, '{MemberId}', '{MemberId}', '{createdAt}', 1);
        """;

    private static string Order(string id, string createdAt) => $"""
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version, client_id, advisor_id,
            currency, payment_method, subtotal, tax_percentage, tax_amount, discount_amount, total, bills_to_final_consumer,
            customer_with_retention, party_with_retention, retention_amount, net_total, billing_company_id, billing_bank_name,
            billing_account_number, billing_account_currency, billing_company_country)
        VALUES (
            '{id}', '{TenantId}', 'PED-{id[^5..]}', NULL, 'Pending', 'PaymentPending', NULL, '{createdAt}', '{MemberId}', NULL, NULL,
            NULL, '{createdAt}', '{createdAt}', 1, '{MemberId}', '{MemberId}', 'COP', 'Transferencia', 100000.00, 19.00,
            19000.00, 0.00, 119000.00, false, false, NULL, 0.00, 119000.00, '{MemberId}', 'Bancolombia', '7542', 'COP', 'CO');
        """;

    private static string Withholding(string table, string documentId, string source, int position, decimal? taxUnitValue) => $"""
        INSERT INTO quotations.{table} (
            document_id, source, position, tax_rate_id, name, type, country, percentage, catalog_percentage, minimum_base,
            minimum_base_unit, tax_unit_value, base_amount, amount, applied)
        VALUES (
            '{documentId}', '{source}', {position}, gen_random_uuid(), 'Retefuente servicios 4 %', 'Retefuente', 'CO', 4.000,
            4.000, {(taxUnitValue is null ? "NULL, NULL, NULL" : $"4.00, 'UVT', {taxUnitValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")},
            0.00, 0.00, false);
        """;

    private static Task<string[]> YearsAsync(string connectionString, string table) =>
        ListAsync(
            connectionString,
            "SELECT document_id || '|' || source || '|' || position || '|' || coalesce(tax_unit_year::text, '') "
            + $"FROM quotations.{table} ORDER BY document_id, source, position");

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

    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }
}
