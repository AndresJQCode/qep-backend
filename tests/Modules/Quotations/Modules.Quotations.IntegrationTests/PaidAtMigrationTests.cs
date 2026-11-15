using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El relleno de <c>paid_at</c> de <c>AddLineCostAndPaidAt</c> y el congelado sin costo de las líneas ya pagadas de
/// <c>FreezePreDeployPaidLinesWithoutCost</c> (spec costo de producto §4.4), contra pedidos sembrados
/// con el esquema anterior. Migra sólo Quotations con <see cref="IMigrator"/>, como
/// <see cref="OrderStatusAxesMigrationTests"/>: el host de pruebas migra todo a la última al arrancar y no quedaría
/// esquema anterior donde sembrar.
/// </summary>
public sealed class PaidAtMigrationTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000c001";
    private const string Member = "01900000-0000-7000-8000-00000000c0a1";

    private const string PaidMarked = "01900000-0000-7000-8000-00000000c101";
    private const string PaidByPlatform = "01900000-0000-7000-8000-00000000c102";
    private const string Pending = "01900000-0000-7000-8000-00000000c103";

    [Fact]
    public async Task PaidAtBackfillUsesPaidMarkedAtThenConvertedAt()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationId(context), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, SeedSql);

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                $"{PaidMarked}|2026-11-01 15:00:00+00",
                $"{PaidByPlatform}|2026-10-01 15:00:00+00",
                $"{Pending}|<null>",
            ],
            await RowsAsync(
                connectionString,
                "SELECT id::text, (paid_at AT TIME ZONE 'UTC')::text || '+00' FROM quotations.orders ORDER BY id"));
        // Lo vendido antes de la migración queda congelado sin costo, con la fecha de pago: su costo nunca se
        // registró y no se inventa. Lo no pagado sigue sin congelar.
        Assert.Equal(
            [
                $"{PaidMarked}|2026-11-01 15:00:00+00|<null>|<null>",
                $"{PaidByPlatform}|2026-10-01 15:00:00+00|<null>|<null>",
                $"{Pending}|<null>|<null>|<null>",
            ],
            await RowsAsync(
                connectionString,
                "SELECT order_id::text, (cost_captured_at AT TIME ZONE 'UTC')::text || '+00', unit_cost::text, " +
                "cost_source FROM quotations.order_items ORDER BY order_id"));
    }

    // Bug del review final: sin marca de congelado, volver a marcar pagado un pedido de antes del despliegue le
    // ponía el costo de hoy a una línea vendida cuando ese costo no existía.
    [Fact]
    public async Task APaidOrderFromBeforeTheMigrationStaysUncostedWhenMarkedPaidAgain()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigrationId(context), TestContext.Current.CancellationToken);
            await ExecuteAsync(connectionString, SeedSql);
            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext(connectionString))
        {
            var order = await context.Orders
                .Include(value => value.Items)
                .Include(value => value.PaymentProofs)
                .SingleAsync(value => value.Id == new OrderId(Guid.Parse(PaidMarked)), TestContext.Current.CancellationToken);
            var variantId = order.Items.Single().VariantId;
            order.UnmarkPaid(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero));
            order.MarkPaid(
                new MemberId(Guid.Parse(Member)),
                requirePaymentProof: false,
                OrderLineCosts.Of([variantId], new Dictionary<Guid, decimal> { [variantId] = 50000m }),
                new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            ["2026-11-01 15:00:00+00|<null>|<null>"],
            await RowsAsync(
                connectionString,
                "SELECT (cost_captured_at AT TIME ZONE 'UTC')::text || '+00', unit_cost::text, cost_source " +
                $"FROM quotations.order_items WHERE order_id = '{PaidMarked}'"));
    }

    private static string OrderValues(
        string id, int number, string paymentStatus, string source, string? paidMarkedAt, string convertedAt) =>
        $"""
        ('{id}', '{TenantId}', 'PED-2026-{number:0000}', NULL, 'Open', '{paymentStatus}', '{convertedAt}',
         '{Member}', {(paidMarkedAt is null ? "NULL" : $"'{paidMarkedAt}'")}, {(paidMarkedAt is null ? "NULL" : $"'{Member}'")},
         '{convertedAt}', '{convertedAt}', 1, '{source}', NULL, NULL,
         '01900000-0000-7000-8000-00000000c0c1', '{Member}', 'COP', '01900000-0000-7000-8000-00000000c0d1', 'Bancolombia',
         '7542', 'COP', 'Transferencia', false, NULL, false, false, false, NULL, NULL, false, false,
         100000.00, 19.00, 19000.00, 0.00, 119000.00, 0.00, 119000.00)
        """;

    private static string ItemValues(string orderId, int number) =>
        $"""
        ('01900000-0000-7000-8000-00000000c2{number:00}', '{orderId}', '01900000-0000-7000-8000-00000000c3{number:00}',
         '01900000-0000-7000-8000-00000000c3{number:00}', 'VS-1', 'Vela de soja', 1.00, 119000.00, 'Own', 0.00, 0.00,
         100000.00, 19, 19000.00, 1, '2026-09-10T15:00:00Z', '2026-09-10T15:00:00Z')
        """;

    // Tres pedidos: pagado con su marca, pagado por la plataforma (sin marca) y pendiente.
    private static readonly string SeedSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, converted_at, converted_by,
            paid_marked_at, paid_marked_by, created_at, updated_at, version, source,
            external_reference, external_key, client_id, advisor_id, currency,
            billing_company_id, billing_bank_name, billing_account_number, billing_account_currency,
            payment_method, is_retail, global_scale_floor, billing_uses_business_name, is_store_pickup,
            bills_to_final_consumer, party_with_retention, party_vat_surplus, customer_with_retention,
            customer_vat_surplus, subtotal, tax_percentage, tax_amount, discount_amount, total,
            retention_amount, net_total)
        VALUES
        {OrderValues(PaidMarked, 1, "FullPaymentReceived", "Manual", "2026-11-01T15:00:00Z", "2026-09-10T15:00:00Z")},
        {OrderValues(PaidByPlatform, 2, "FullPaymentReceived", "Shopify", null, "2026-10-01T15:00:00Z")},
        {OrderValues(Pending, 3, "PaymentPending", "Manual", null, "2026-09-12T15:00:00Z")};
        INSERT INTO quotations.order_items (
            id, order_id, variant_id, product_id, product_code, product_name, quantity, unit_price, discount_origin,
            discount_percentage, discount_amount, subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        VALUES
        {ItemValues(PaidMarked, 1)},
        {ItemValues(PaidByPlatform, 2)},
        {ItemValues(Pending, 3)};
        RESET session_replication_role;
        """;

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>La migración justo antes de <c>AddLineCostAndPaidAt</c>, por orden y no por nombre: así la prueba
    /// no se rompe si otra rama mete una migración en el medio al mergear.</summary>
    private static string PreviousMigrationId(QuotationsDbContext context)
    {
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(id => id.EndsWith("_AddLineCostAndPaidAt", StringComparison.Ordinal));
        Assert.True(index > 0);
        return migrations[index - 1];
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

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

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
