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

    // Spec 2026-10-15: el pedido copia su cotización al migrar. Se siembra con el esquema anterior a
    // AddOrderOwnData, igual que el rename: FK apagada, cotización mínima pero con cuenta de cobro,
    // una línea y una parte de envío, que es lo que la copia tiene que arrastrar.
    private const string LastMigrationBeforeOwnData = "20261009045958_AddQuotationSettings";

    private const string SourceQuotationId = "01900000-0000-7000-8000-00000000c004";

    private const string QuotationWithLinesSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number,
            billing_account_currency)
        VALUES (
            '{SourceQuotationId}', '{TenantId}', 'COT-2026-0007', '01900000-0000-7000-8000-00000000c007',
            '{MemberId}', 'Sent', '2026-09-09T15:00:00Z', '2026-10-09', 'COP', 'Transferencia',
            100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, true, false, 2500.00, 116500.00,
            'Entregar en portería', '{MemberId}', '{MemberId}', '2026-09-09T15:00:00Z', 2, '01900000-0000-7000-8000-00000000c008',
            'Bancolombia', '7542', 'COP');
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, product_code, product_name, quantity, unit_price, discount_origin, discount_percentage,
            discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        VALUES (
            '01900000-0000-7000-8000-00000000c009', '{SourceQuotationId}', '01900000-0000-7000-8000-00000000c00a',
            'ABC', 'Aceite', 10.00, 11900.00, 'Own', 0.00, 0.00, 100000.00, 19, 19000.00, 1, '2026-09-09T15:00:00Z', '2026-09-09T15:00:00Z');
        INSERT INTO quotations.quotation_parties (id, quotation_id, role, name, phone, email, address, department_id, city_id)
        VALUES (
            '01900000-0000-7000-8000-00000000c00b', '{SourceQuotationId}', 'Shipping', 'Bodega', '3001234567',
            'bodega@x.co', 'Cl 1', '01900000-0000-7000-8000-00000000c00c', '01900000-0000-7000-8000-00000000c00d');
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'PED-2026-0001', '{SourceQuotationId}', 'Pending', 'PaymentPending',
            'Pago verificado', '2026-09-10T15:00:00Z', '{MemberId}', NULL, NULL, NULL, '2026-09-10T15:00:00Z',
            '2026-09-10T15:00:00Z', 1);
        RESET session_replication_role;
        """;

    [Fact]
    public async Task AddOrderOwnDataCopiesTheQuotationIntoTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationWithLinesSql);

        await migrator.MigrateAsync(MigrationId(context, "_AddOrderOwnData"), TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Parse("01900000-0000-7000-8000-00000000c007"), await ScalarAsync<Guid>(
            connectionString, $"SELECT client_id FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(116500.00m, await ScalarAsync<decimal>(
            connectionString, $"SELECT net_total FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal("Entregar en portería\n\nPago verificado", await ScalarAsync<string>(
            connectionString, $"SELECT notes FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal("Quotation", await ScalarAsync<string>(
            connectionString, $"SELECT source FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal("Bancolombia", await ScalarAsync<string>(
            connectionString, $"SELECT billing_bank_name FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM quotations.order_items WHERE order_id = '{OrderId}' AND product_code = 'ABC'"));
        Assert.Equal("Bodega", await ScalarAsync<string>(
            connectionString, $"SELECT name FROM quotations.order_parties WHERE order_id = '{OrderId}' AND role = 'Shipping'"));
        Assert.Equal("Created", await ScalarAsync<string>(
            connectionString, $"SELECT event_type FROM quotations.order_history WHERE order_id = '{OrderId}'"));
        Assert.Equal("Converted", await ScalarAsync<string>(
            connectionString, $"SELECT status FROM quotations.quotations WHERE id = '{SourceQuotationId}'"));
    }

    [Fact]
    public async Task AddOrderOwnDataRefusesAnOrderWhoseQuotationHasNoLines()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationWithLinesSql);
        await ExecuteAsync(connectionString, "DELETE FROM quotations.quotation_items");

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync(MigrationId(context, "_AddOrderOwnData"), TestContext.Current.CancellationToken));

        Assert.Contains(OrderId, error.MessageText, StringComparison.Ordinal);
    }

    private const string OrdersJobId = "01900000-0000-7000-8000-00000000c007";
    private const string QuotationsJobId = "01900000-0000-7000-8000-00000000c008";

    // Un job de pedidos pendiente, tal como lo dejó el código anterior al rename, y uno de
    // cotizaciones que la migración no tiene que tocar.
    private const string LegacyExportJobsSql = $$"""
        INSERT INTO quotations.export_jobs (
            id, tenant_id, requested_by, kind, filters, status, attempts, next_attempt_at, requested_at)
        VALUES
            ('{{OrdersJobId}}', '{{TenantId}}', '{{MemberId}}', 'Sales',
             '{"ClientId":null,"AdvisorId":null,"Status":null,"PaymentStatus":null,"ConvertedFrom":"2026-09-01","ConvertedTo":"2026-09-12","ClientCuc":null,"SaleNumber":"VEN-2026"}',
             'Pending', 0, now(), now()),
            ('{{QuotationsJobId}}', '{{TenantId}}', '{{MemberId}}', 'Quotations',
             '{"ClientId":null,"QuotationNumber":"COT-2026"}',
             'Pending', 0, now(), now());
        """;

    [Fact]
    public async Task PendingOrderExportsKeepTheirKindAndTheirNumberFilter()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyExportJobsSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_MigrateExportJobsToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("Orders", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("VEN-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'OrderNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.False(await ScalarAsync<bool>(
            connectionString, $"SELECT filters ? 'SaleNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("2026-09-01", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'ConvertedFrom' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("Quotations", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{QuotationsJobId}'"));
        Assert.Equal("COT-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'QuotationNumber' FROM quotations.export_jobs WHERE id = '{QuotationsJobId}'"));
    }

    [Fact]
    public async Task RevertingPutsBackTheOldKindAndKey()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyExportJobsSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_MigrateExportJobsToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("Sales", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("VEN-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'SaleNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.False(await ScalarAsync<bool>(
            connectionString, $"SELECT filters ? 'OrderNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
    }

    // Spec de Shopify D-S23: los pedidos que ya existían no tienen clave externa ni título de envío, y el índice
    // único es parcial para que todos esos nulos no choquen entre sí.
    [Fact]
    public async Task AddOrderExternalKeyLeavesExistingOrdersWithoutKeyOrTitle()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeOwnData, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, QuotationWithLinesSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_AddDefaultFreightChargeMode"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(MigrationId(context, "_AddOrderExternalKey"), TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM quotations.orders WHERE id = '{OrderId}' AND (external_key IS NOT NULL OR external_shipping_title IS NOT NULL)"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM quotations.orders WHERE id = '{OrderId}'"));
        var definition = await ScalarAsync<string>(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'quotations' AND indexname = 'IX_orders_tenant_external_key'");
        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, source, external_key)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (external_key IS NOT NULL)", definition, StringComparison.Ordinal);
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
