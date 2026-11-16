using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El relleno de <c>order_date</c> de <c>AddOrderDate</c> (spec 2026-11-16 §6 y §7): el día local de <c>converted_at</c> en el
/// huso del tenant, Bogotá si no tiene, y Bogotá también para un pedido sin fila de tenant (la guarda de la migración). Migra
/// sólo Quotations sobre una base vacía, como <see cref="PdfContentMigrationTests"/>, con una réplica mínima de
/// <c>tenancy.tenants</c> que sólo trae lo que la migración lee.
/// </summary>
public sealed class OrderDateMigrationTests
{
    private const string BogotaTenant = "01900000-0000-7000-8000-0000000d0a01";
    private const string TokyoTenant = "01900000-0000-7000-8000-0000000d0a02";
    private const string NoZoneTenant = "01900000-0000-7000-8000-0000000d0a03";
    private const string MissingTenant = "01900000-0000-7000-8000-0000000d0a04";
    private const string MemberId = "01900000-0000-7000-8000-0000000d0f01";

    private const string BogotaOrder = "01900000-0000-7000-8000-0000000d0401";
    private const string TokyoOrder = "01900000-0000-7000-8000-0000000d0402";
    private const string NoZoneOrder = "01900000-0000-7000-8000-0000000d0403";
    private const string OrphanOrder = "01900000-0000-7000-8000-0000000d0404";

    // 2026-11-14T02:00Z es el 13 en Bogotá (UTC-5) y el 14 en Tokio (UTC+9).
    private const string ConvertedAt = "2026-11-14T02:00:00Z";

    [Fact]
    public async Task BackfillsTheLocalDayOfTheConversionInTheTenantTimeZone()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith("_AddOrderDate", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], TestContext.Current.CancellationToken);

        await ExecuteAsync(connectionString, $"""
            CREATE SCHEMA IF NOT EXISTS tenancy;
            CREATE TABLE tenancy.tenants (id uuid PRIMARY KEY, time_zone text NULL);
            INSERT INTO tenancy.tenants (id, time_zone) VALUES
                ('{BogotaTenant}', 'America/Bogota'),
                ('{TokyoTenant}', 'Asia/Tokyo'),
                ('{NoZoneTenant}', NULL);
            SET session_replication_role = replica;
            {Order(BogotaOrder, BogotaTenant)}
            {Order(TokyoOrder, TokyoTenant)}
            {Order(NoZoneOrder, NoZoneTenant)}
            {Order(OrphanOrder, MissingTenant)}
            RESET session_replication_role;
            """);

        await migrator.MigrateAsync(target, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                $"{BogotaOrder}|2026-11-13",
                $"{TokyoOrder}|2026-11-14",
                $"{NoZoneOrder}|2026-11-13",
                $"{OrphanOrder}|2026-11-13",
            },
            await ListAsync(
                connectionString,
                "SELECT id::text || '|' || order_date::text FROM quotations.orders ORDER BY id"));
        Assert.Equal("NO", await ScalarAsync<string>(
            connectionString,
            "SELECT is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'quotations' AND table_name = 'orders' AND column_name = 'order_date'"));
    }

    // Expand/contract: un pod viejo inserta sin order_date durante el rollout y el default de la base lo llena.
    [Fact]
    public async Task AnInsertWithoutTheOrderDateGetsTheDatabaseDefault()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(connectionString, $"""
            SET session_replication_role = replica;
            {Order(BogotaOrder, BogotaTenant)}
            RESET session_replication_role;
            """);

        Assert.Equal(
            await ScalarAsync<string>(connectionString, "SELECT ((now() AT TIME ZONE 'America/Bogota')::date)::text"),
            await ScalarAsync<string>(
                connectionString, $"SELECT order_date::text FROM quotations.orders WHERE id = '{BogotaOrder}'"));
    }

    // Las columnas de TaxUnitYearMigrationTests: las obligatorias de quotations.orders antes de AddOrderDate, sin order_date,
    // que es lo que escribe un pod viejo.
    private static string Order(string id, string tenantId) => $"""
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version, client_id, advisor_id,
            currency, payment_method, subtotal, tax_percentage, tax_amount, discount_amount, total, bills_to_final_consumer,
            customer_with_retention, party_with_retention, retention_amount, net_total, billing_company_id, billing_bank_name,
            billing_account_number, billing_account_currency, billing_company_country)
        VALUES (
            '{id}', '{tenantId}', 'PED-{id[^5..]}', NULL, 'Open', 'PaymentPending', NULL, '{ConvertedAt}', '{MemberId}', NULL,
            NULL, NULL, '{ConvertedAt}', '{ConvertedAt}', 1, '{MemberId}', '{MemberId}', 'COP', 'Transferencia', 100000.00,
            19.00, 19000.00, 0.00, 119000.00, false, false, NULL, 0.00, 119000.00, '{MemberId}', 'Bancolombia', '7542', 'COP',
            'CO');
        """;

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
