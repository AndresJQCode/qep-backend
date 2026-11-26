using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración de los ejes del pedido (spec 2026-11-10 §8), contra pedidos sembrados con el esquema anterior
/// (<c>Pending</c>/<c>Approved</c>/<c>Invoiced</c> y <c>approved_*</c>). Migra sólo Quotations y hasta una migración
/// puntual con <see cref="IMigrator"/>, como <see cref="FreightMigrationTests"/>: el host de pruebas migra todo a la
/// última al arrancar y no quedaría esquema anterior donde sembrar. La que baja con el Down vuelve a la última al
/// terminar, como pide CLAUDE.md, aunque ninguna llama a la API.
/// </summary>
public sealed class OrderStatusAxesMigrationTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000e001";
    private const string MemberA = "01900000-0000-7000-8000-00000000e0a1";
    private const string MemberB = "01900000-0000-7000-8000-00000000e0b1";
    private const string ApprovedAt = "2026-09-11T15:00:00Z";
    private const string InvoicedAt = "2026-09-12T15:00:00Z";

    private const string PendingUnpaid = "01900000-0000-7000-8000-00000000e101";
    private const string ApprovedPaid = "01900000-0000-7000-8000-00000000e102";
    private const string InvoicedPaid = "01900000-0000-7000-8000-00000000e103";
    private const string PendingPaidManual = "01900000-0000-7000-8000-00000000e104";
    private const string PendingPaidShopify = "01900000-0000-7000-8000-00000000e105";
    private const string ApprovedPartial = "01900000-0000-7000-8000-00000000e106";
    private const string CancelledOrder = "01900000-0000-7000-8000-00000000e107";
    private const string PendingPaidWithoutProofs = "01900000-0000-7000-8000-00000000e108";
    private const string PendingPaidPartialProofs = "01900000-0000-7000-8000-00000000e109";
    private const string PendingPaidCopRounded = "01900000-0000-7000-8000-00000000e10a";
    private const string PendingPaidUsdCents = "01900000-0000-7000-8000-00000000e10b";
    private const string PendingPaidShopifyWithProof = "01900000-0000-7000-8000-00000000e10c";

    [Fact]
    public async Task PendingApprovedAndInvoicedBecomeOpenAndKeepTheInvoicing()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        foreach (var id in new[] { PendingUnpaid, ApprovedPaid, InvoicedPaid })
        {
            Assert.Equal("Open", await TextAsync(connectionString, $"SELECT status FROM quotations.orders WHERE id = '{id}'"));
        }

        Assert.Equal("Cancelled", await TextAsync(connectionString, $"SELECT status FROM quotations.orders WHERE id = '{CancelledOrder}'"));
        Assert.Equal(
            Utc(InvoicedAt),
            await ScalarAsync<DateTime>(connectionString, $"SELECT invoiced_at FROM quotations.orders WHERE id = '{InvoicedPaid}'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.orders WHERE fulfillment_status <> 'NotPrepared'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.orders WHERE archived_at IS NOT NULL OR archived_by IS NOT NULL"));
    }

    // Owner, 2026-11-11: la migración no pierde nada. approved_* se quedan como históricas, el estado y el pago de
    // antes quedan respaldados en legacy_*, y nada fuera de los ejes cambia.
    [Fact]
    public async Task UpKeepsTheApprovalAndBacksUpTheOldStatuses()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderBuyerAccount"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SeedSql);
        var seeded = await RowsAsync(connectionString, OrderAxesSql);
        var untouchedBefore = await RowsAsync(connectionString, UntouchedSql);

        await migrator.MigrateAsync(MigrationId(context, "_OrderStatusAxes"), TestContext.Current.CancellationToken);

        Assert.Equal(
            seeded,
            await RowsAsync(
                connectionString,
                "SELECT id::text, legacy_status, legacy_payment_status, approved_at::text, approved_by::text " +
                "FROM quotations.orders ORDER BY id"));
        Assert.Equal(untouchedBefore, await RowsAsync(connectionString, UntouchedSql));
        foreach (var column in new[] { "approved_at", "approved_by", "legacy_status", "legacy_payment_status" })
        {
            Assert.StartsWith(
                "Histórico",
                await TextAsync(
                    connectionString,
                    $"SELECT col_description('quotations.orders'::regclass, attnum) FROM pg_attribute " +
                    $"WHERE attrelid = 'quotations.orders'::regclass AND attname = '{column}'"));
        }
    }

    [Fact]
    public async Task ApprovedPaidOrdersTakeTheirPaidMarkFromTheApproval()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        foreach (var (id, member) in new[] { (ApprovedPaid, MemberA), (InvoicedPaid, MemberB) })
        {
            var where = $"FROM quotations.orders WHERE id = '{id}'";
            Assert.Equal("FullPaymentReceived", await TextAsync(connectionString, $"SELECT payment_status {where}"));
            Assert.Equal(Guid.Parse(member), await ScalarAsync<Guid>(connectionString, $"SELECT paid_marked_by {where}"));
            Assert.Equal(
                Utc(ApprovedAt),
                await ScalarAsync<DateTime>(connectionString, $"SELECT paid_marked_at {where}"));
        }
    }

    // Un «Pago total» que nadie aprobó vuelve a lo que digan sus comprobantes, como CalculatedPaymentStatus: sin
    // comprobantes, pendiente; con menos que el neto (redondeado como lo muestra la moneda), parcial; con lo que lo
    // cubre, por verificar. Nunca queda pagado.
    [Fact]
    public async Task AnUnapprovedFullPaymentIsRecalculatedFromItsProofs()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        foreach (var (id, expected) in new[]
        {
            (PendingPaidManual, "PendingVerification"),
            (PendingPaidWithoutProofs, "PaymentPending"),
            (PendingPaidPartialProofs, "PartialPaymentReceived"),
            (PendingPaidCopRounded, "PendingVerification"),
            (PendingPaidUsdCents, "PartialPaymentReceived"),
        })
        {
            var where = $"FROM quotations.orders WHERE id = '{id}'";
            Assert.Equal((id, expected), (id, await TextAsync(connectionString, $"SELECT payment_status {where}")));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {where} AND paid_marked_at IS NULL"));
        }
    }

    // Un pedido de Shopify con comprobantes de QEP: el «Pago total» lo eligió la asesora, no lo sincronizó la tienda
    // (SyncExternalPaymentStatus no corre con comprobantes), así que se recalcula como uno manual.
    [Fact]
    public async Task AShopifyOrderWithQepProofsIsRecalculatedFromThem()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        Assert.Equal(
            "PendingVerification",
            await TextAsync(
                connectionString, $"SELECT payment_status FROM quotations.orders WHERE id = '{PendingPaidShopifyWithProof}'"));
    }

    // Review Focus 1: el pago sincronizado de Shopify no lo marcó una persona; lo dice la tienda.
    [Fact]
    public async Task AShopifyPaymentSyncedAsFullKeepsItsValue()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        Assert.Equal(
            "FullPaymentReceived",
            await TextAsync(connectionString, $"SELECT payment_status FROM quotations.orders WHERE id = '{PendingPaidShopify}'"));
        Assert.Equal(
            "Open",
            await TextAsync(connectionString, $"SELECT status FROM quotations.orders WHERE id = '{PendingPaidShopify}'"));
    }

    [Fact]
    public async Task AnApprovedOrderWithoutFullPaymentReopens()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        var where = $"FROM quotations.orders WHERE id = '{ApprovedPartial}'";
        Assert.Equal("Open", await TextAsync(connectionString, $"SELECT status {where}"));
        Assert.Equal("PartialPaymentReceived", await TextAsync(connectionString, $"SELECT payment_status {where}"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) {where} AND paid_marked_at IS NULL AND paid_marked_by IS NULL"));
    }

    [Fact]
    public async Task TheTenantSettingsGetTheOwnersDefaults()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateSeededAsync(connectionString);

        var where = $"FROM quotations.tenant_quotation_settings WHERE tenant_id = '{TenantId}'";
        Assert.False(await ScalarAsync<bool>(connectionString, $"SELECT require_payment_proof_to_mark_paid {where}"));
        Assert.True(await ScalarAsync<bool>(connectionString, $"SELECT require_full_payment_to_invoice {where}"));
        Assert.False(await ScalarAsync<bool>(connectionString, $"SELECT auto_archive_orders {where}"));
    }

    // Owner, 2026-11-11: el Down es exacto. Cada fila vuelve byte a byte a como estaba antes del Up (estado, pago,
    // approved_* y todo lo demás), sin filas de más ni de menos, y no queda ninguna columna ni comentario del Up.
    [Fact]
    public async Task DownRestoresEveryRowExactly()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderBuyerAccount"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SeedSql);
        var seededAxes = await RowsAsync(connectionString, OrderAxesSql);
        var seededRows = await RowsAsync(connectionString, WholeOrdersSql);
        var untouchedBefore = await RowsAsync(connectionString, UntouchedSql);

        await migrator.MigrateAsync(MigrationId(context, "_OrderStatusAxes"), TestContext.Current.CancellationToken);
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderBuyerAccount"), TestContext.Current.CancellationToken);

        Assert.Equal(seededAxes, await RowsAsync(connectionString, OrderAxesSql));
        Assert.Equal(seededRows, await RowsAsync(connectionString, WholeOrdersSql));
        Assert.Equal(untouchedBefore, await RowsAsync(connectionString, UntouchedSql));
        Assert.Equal(12L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM quotations.orders"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'quotations' AND " +
            "((table_name = 'orders' AND column_name IN ('fulfillment_status', 'paid_marked_at', 'paid_marked_by', " +
            "'prepared_at', 'prepared_by', 'archived_at', 'archived_by', 'legacy_status', 'legacy_payment_status')) OR " +
            "(table_name = 'tenant_quotation_settings' AND column_name IN ('require_payment_proof_to_mark_paid', " +
            "'require_full_payment_to_invoice', 'auto_archive_orders')))"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_attribute WHERE attrelid = 'quotations.orders'::regclass " +
            "AND attname IN ('approved_at', 'approved_by') AND col_description(attrelid, attnum) IS NOT NULL"));

        // CLAUDE.md: una prueba que baja a una versión vieja vuelve a la última.
        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
    }

    // Antes del Up: el estado, el pago y la aprobación de cada pedido, con el mismo orden de columnas que la consulta
    // de respaldo del Up.
    private const string OrderAxesSql =
        "SELECT id::text, status, payment_status, approved_at::text, approved_by::text FROM quotations.orders ORDER BY id";

    // La fila completa del pedido, sin depender del orden de las columnas.
    private const string WholeOrdersSql = "SELECT to_jsonb(o)::text FROM quotations.orders o ORDER BY id";

    // Lo que la migración no debe tocar: las cotizaciones y los comprobantes.
    private const string UntouchedSql =
        "SELECT 'q:' || to_jsonb(q)::text FROM quotations.quotations q " +
        "UNION ALL SELECT 'p:' || to_jsonb(p)::text FROM quotations.order_payment_proofs p ORDER BY 1";

    // Siembra con el esquema de _AddOrderBuyerAccount (las columnas NOT NULL de AddOrderOwnData incluidas) y sube a
    // _OrderStatusAxes.
    private static async Task MigrateSeededAsync(string connectionString)
    {
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddOrderBuyerAccount"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SeedSql);
        await migrator.MigrateAsync(MigrationId(context, "_OrderStatusAxes"), TestContext.Current.CancellationToken);
    }

    private static string OrderValues(
        string id, int number, string status, string paymentStatus, string source, string? approvedBy, string? invoicedAt,
        string? externalKey = null, string currency = "COP", string netTotal = "116500.00") =>
        $"""
        ('{id}', '{TenantId}', 'PED-2026-{number:0000}', NULL, '{status}', '{paymentStatus}', '2026-09-10T15:00:00Z',
         '{MemberA}', {(approvedBy is null ? "NULL" : $"'{ApprovedAt}'")}, {(approvedBy is null ? "NULL" : $"'{approvedBy}'")},
         {(invoicedAt is null ? "NULL" : $"'{invoicedAt}'")}, {(invoicedAt is null ? "NULL" : $"'{MemberB}'")},
         '2026-09-10T15:00:00Z', '2026-09-10T15:00:00Z', 1, '{source}',
         {(externalKey is null ? "NULL" : $"'#{number}'")}, {(externalKey is null ? "NULL" : $"'{externalKey}'")},
         '01900000-0000-7000-8000-00000000e0c1', '{MemberA}', '{currency}', '01900000-0000-7000-8000-00000000e0d1', 'Bancolombia',
         '7542', 'COP', 'Transferencia', false, NULL, false, false, false, NULL, NULL, true, false,
         100000.00, 19.00, 19000.00, 0.00, 119000.00, 2500.00, {netTotal})
        """;

    private static string ProofValues(string orderId, int number, string amount) =>
        $"""
        ('01900000-0000-7000-8000-00000000f{number:000}', '{orderId}', '01900000-0000-7000-8000-00000000f9{number:00}',
         {amount}, '{MemberA}', '2026-09-10T15:00:00Z')
        """;

    private static readonly string SeedSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.tenant_quotation_settings (tenant_id, minimum_units) VALUES ('{TenantId}', 6);
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, converted_at, converted_by,
            approved_at, approved_by, invoiced_at, invoiced_by, created_at, updated_at, version, source,
            external_reference, external_key, client_id, advisor_id, currency,
            billing_company_id, billing_bank_name, billing_account_number, billing_account_currency,
            payment_method, is_retail, global_scale_floor, billing_uses_business_name, is_store_pickup,
            bills_to_final_consumer, party_with_retention, party_vat_surplus, customer_with_retention,
            customer_vat_surplus, subtotal, tax_percentage, tax_amount, discount_amount, total,
            retention_amount, net_total)
        VALUES
        {OrderValues(PendingUnpaid, 1, "Pending", "PaymentPending", "Manual", null, null)},
        {OrderValues(ApprovedPaid, 2, "Approved", "FullPaymentReceived", "Quotation", MemberA, null)},
        {OrderValues(InvoicedPaid, 3, "Invoiced", "FullPaymentReceived", "Manual", MemberB, InvoicedAt)},
        {OrderValues(PendingPaidManual, 4, "Pending", "FullPaymentReceived", "Manual", null, null)},
        {OrderValues(PendingPaidShopify, 5, "Pending", "FullPaymentReceived", "Shopify", null, null, "shop-1:5005")},
        {OrderValues(ApprovedPartial, 6, "Approved", "PartialPaymentReceived", "Manual", MemberA, null)},
        {OrderValues(CancelledOrder, 7, "Cancelled", "PaymentPending", "Manual", null, null)},
        {OrderValues(PendingPaidWithoutProofs, 8, "Pending", "FullPaymentReceived", "Manual", null, null)},
        {OrderValues(PendingPaidPartialProofs, 9, "Pending", "FullPaymentReceived", "Manual", null, null)},
        {OrderValues(PendingPaidCopRounded, 10, "Pending", "FullPaymentReceived", "Manual", null, null, netTotal: "116529.37")},
        {OrderValues(PendingPaidUsdCents, 11, "Pending", "FullPaymentReceived", "Manual", null, null, currency: "USD", netTotal: "100.37")},
        {OrderValues(PendingPaidShopifyWithProof, 12, "Pending", "FullPaymentReceived", "Shopify", null, null, "shop-1:5012")};
        INSERT INTO quotations.order_payment_proofs (id, order_id, file_id, amount, uploaded_by, uploaded_at)
        VALUES
        {ProofValues(PendingPaidManual, 1, "116500.00")},
        {ProofValues(PendingPaidPartialProofs, 2, "50000.00")},
        {ProofValues(PendingPaidCopRounded, 3, "116529.00")},
        {ProofValues(PendingPaidUsdCents, 4, "100.00")},
        {ProofValues(PendingPaidShopifyWithProof, 5, "60000.00")},
        {ProofValues(PendingPaidShopifyWithProof, 6, "56500.00")};
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, updated_at, version, billing_company_id, billing_bank_name, billing_account_number,
            billing_account_currency)
        VALUES (
            '01900000-0000-7000-8000-00000000e201', '{TenantId}', 'COT-2026-0201', '01900000-0000-7000-8000-00000000e0c1',
            '{MemberA}', 'Converted', '2026-09-09T15:00:00Z', '2026-10-09', 'COP', 'Transferencia',
            100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, true, false, 2500.00, 116500.00,
            'Aprobada', '{MemberA}', '{MemberA}', '2026-09-09T15:00:00Z', 3, '01900000-0000-7000-8000-00000000e0d1',
            'Bancolombia', '7542', 'COP');
        RESET session_replication_role;
        """;

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

    // Npgsql lee timestamptz como DateTime en UTC.
    private static DateTime Utc(string value) =>
        DateTime.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static async Task<IReadOnlyList<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? "<null>" : reader.GetString(i);
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private static Task<string> TextAsync(string connectionString, string sql) => ScalarAsync<string>(connectionString, sql);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
