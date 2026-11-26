using Modules.Siigo.Infrastructure.Persistence.Migrations;
using Npgsql;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>Spec 2026-11-24, «Datos y migraciones»: el SQL a mano de <c>AddSiigoBackgroundInvoicing</c>, corrido contra la base ya
/// migrada.</summary>
public sealed class SiigoBackgroundInvoicingMigrationTests
{
    [Fact]
    public async Task TheBackfillMarksOnlyPendingRowsThatWentOutAndRunsTwiceTheSame()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var timedOut = await InsertInvoiceAsync(connectionString, "Pending", "siigo.invoice.timeout", sending: false);
        var inFlight = await InsertInvoiceAsync(connectionString, "Pending", null, sending: true);
        var neverSent = await InsertInvoiceAsync(connectionString, "Pending", null, sending: false);
        var rejected = await InsertInvoiceAsync(connectionString, "Rejected", "siigo.invoice.siigo_service", sending: false);

        await ExecuteAsync(connectionString, AddSiigoBackgroundInvoicing.BackfillSql);
        await ExecuteAsync(connectionString, AddSiigoBackgroundInvoicing.BackfillSql);

        Assert.True(await MarkedWithUpdatedAtAsync(connectionString, timedOut));
        Assert.True(await MarkedWithUpdatedAtAsync(connectionString, inFlight));
        Assert.True(await NotMarkedAsync(connectionString, neverSent));
        Assert.True(await NotMarkedAsync(connectionString, rejected));
    }

    [Fact]
    public async Task TheOpenOrderGuardRefusesDuplicatesAndPassesWithoutThem()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await ExecuteAsync(connectionString, "DROP INDEX siigo.\"IX_invoice_batch_items_tenant_order_open\"");
        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await InsertBatchItemAsync(connectionString, tenantId, orderId, "Pending");
        var second = await InsertBatchItemAsync(connectionString, tenantId, orderId, "Pending");

        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connectionString, AddSiigoBackgroundInvoicing.OpenOrderGuardSql));

        Assert.Equal("P0001", refused.SqlState);

        await ExecuteAsync(
            connectionString, "UPDATE siigo.invoice_batch_items SET status = 'Failed' WHERE id = @id", ("id", second));
        await ExecuteAsync(connectionString, AddSiigoBackgroundInvoicing.OpenOrderGuardSql);
    }

    private static async Task<Guid> InsertInvoiceAsync(string connectionString, string status, string? errorCode, bool sending)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO siigo.invoices (id, tenant_id, connection_id, order_id, kind, status, idempotency_key, request_json, error_code,
                attempts, order_version, order_changed, sending_since, created_by, created_at, updated_at)
            VALUES (@id, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'Order', @status, 'key', '{}', NULLIF(@errorCode, ''),
                1, 1, false, CASE WHEN @sending THEN now() - interval '2 hours' END, gen_random_uuid(), now() - interval '3 hours',
                now() - interval '1 hour')
            """,
            ("id", id),
            ("status", status),
            ("errorCode", errorCode ?? string.Empty),
            ("sending", sending));
        return id;
    }

    private static Task<bool> MarkedWithUpdatedAtAsync(string connectionString, Guid id) =>
        ScalarAsync<bool>(
            connectionString,
            """
            SELECT unanswered_at IS NOT NULL AND unanswered_at = updated_at AND last_unanswered_at = updated_at
            FROM siigo.invoices WHERE id = @id
            """,
            ("id", id));

    private static Task<bool> NotMarkedAsync(string connectionString, Guid id) =>
        ScalarAsync<bool>(
            connectionString,
            "SELECT unanswered_at IS NULL AND last_unanswered_at IS NULL FROM siigo.invoices WHERE id = @id",
            ("id", id));

    private static async Task<Guid> InsertBatchItemAsync(string connectionString, Guid tenantId, Guid orderId, string status)
    {
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO siigo.invoice_batches (id, tenant_id, requested_by, created_at)
            VALUES (@batchId, @tenantId, gen_random_uuid(), now());
            INSERT INTO siigo.invoice_batch_items (id, batch_id, tenant_id, order_id, position, status, missing_codes, attempts,
                available_at, updated_at)
            VALUES (@itemId, @batchId, @tenantId, @orderId, 0, @status, '', 0, now(), now())
            """,
            ("batchId", batchId),
            ("itemId", itemId),
            ("tenantId", tenantId),
            ("orderId", orderId),
            ("status", status));
        return itemId;
    }
}
