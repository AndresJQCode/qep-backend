using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Modules.ServiceContracts.Infrastructure.Persistence.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// <c>AddPriceAdjustment</c> sobre datos que ya existían (spec reajuste §6.8 y §7): la base se deja en la versión anterior, se
/// siembra por SQL como la dejaría el código viejo, se migra y se corre <c>BackfillSql</c> otra vez, que tiene que ser
/// idempotente. Ningún contrato existente puede quedar retenido ni sin cobrar. Sin host: sólo el contexto del módulo.
/// </summary>
public sealed class ServiceContractsPriceAdjustmentMigrationTests
{
    private const string PreviousMigration = "20261031034952_AddServiceDescription";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task ExistingContractsGetManualAndAnAnniversaryFifteenDaysAhead()
    {
        await using var database = await OpenAtThePreviousVersionAsync();
        var old = UtcToday.AddDays(-400);
        var future = UtcToday.AddDays(40);
        // El aniversario justo a 15 días entra; el que cae a 14 salta al año siguiente. El que entra se ancla cuatro años atrás:
        // un año atrás, un límite que caiga en 29 de febrero daría un ancla del 28 y la aserción dependería del día de la corrida.
        var onTheLimit = UtcToday.AddDays(15).AddYears(-4);
        var justInside = UtcToday.AddDays(14).AddYears(-1);
        var oldId = await InsertServiceAsync(database, old);
        var futureId = await InsertServiceAsync(database, future);
        var onTheLimitId = await InsertServiceAsync(database, onTheLimit);
        var justInsideId = await InsertServiceAsync(database, justInside);

        await MigrateAndBackfillAgainAsync(database);

        foreach (var (id, anchor) in new[] { (oldId, old), (futureId, future), (onTheLimitId, onTheLimit), (justInsideId, justInside) })
        {
            var row = await ReadServiceAsync(database, id);
            Assert.Equal(("Manual", 0m), (row.Mode, row.Points));
            Assert.Null(row.HeldSince);
            var years = Enumerable.Range(1, 200).First(k => anchor.AddYears(k) >= UtcToday.AddDays(15));
            Assert.Equal(anchor.AddYears(years), row.NextReviewDate);
        }

        Assert.Equal(future.AddYears(1), (await ReadServiceAsync(database, futureId)).NextReviewDate);
        Assert.Equal(UtcToday.AddDays(15), (await ReadServiceAsync(database, onTheLimitId)).NextReviewDate);
        Assert.True((await ReadServiceAsync(database, justInsideId)).NextReviewDate > UtcToday.AddDays(300));
        // Ningún contrato existente arrastra períodos retenidos: la columna nueva nace vacía, sin relleno.
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT count(*) FROM billing.services WHERE carried_held_periods = '{}'"));
    }

    [Fact]
    public async Task AFebruaryTwentyNinthAnchorReviewsOnTheTwentyEighth()
    {
        await using var database = await OpenAtThePreviousVersionAsync();
        var anchor = new DateOnly(2024, 2, 29);
        var id = await InsertServiceAsync(database, anchor);

        await MigrateAndBackfillAgainAsync(database);

        var review = (await ReadServiceAsync(database, id)).NextReviewDate;
        var years = Enumerable.Range(1, 200).First(k => anchor.AddYears(k) >= UtcToday.AddDays(15));
        Assert.Equal(anchor.AddYears(years), review);
        Assert.Equal(2, review.Month);
        Assert.Equal(DateTime.IsLeapYear(review.Year) ? 29 : 28, review.Day);
    }

    [Fact]
    public async Task ChargesGetTheirDueDateAsPeriod()
    {
        await using var database = await OpenAtThePreviousVersionAsync();
        var serviceId = await InsertServiceAsync(database, new DateOnly(2026, 1, 10));
        await InsertChargeAsync(database, serviceId, new DateOnly(2026, 1, 10));
        await InsertChargeAsync(database, serviceId, new DateOnly(2026, 2, 10));
        await ExecuteAsync(
            database,
            $"""
            INSERT INTO billing.service_price_changes
                (id, tenant_id, service_id, previous_price, previous_currency, new_price, new_currency, reason, occurred_at)
            VALUES (gen_random_uuid(), gen_random_uuid(), '{serviceId}', 100, 'COP', 120, 'COP', 'Ajuste', now())
            """);

        await MigrateAndBackfillAgainAsync(database);

        Assert.Equal(2L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM billing.charges WHERE period_date = due_date AND NOT price_unreviewed"));
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM billing.service_price_changes WHERE origin = 'Edit' AND schedule_id IS NULL"));
        // El único pasó del vencimiento al período.
        Assert.Equal(0L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'billing' AND indexname = 'IX_charges_service_due'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'billing' AND indexname = 'IX_charges_service_period'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            database,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'billing' AND is_nullable = 'YES'
              AND column_name IN ('period_date', 'next_review_date', 'adjustment_mode', 'origin')
            """));
    }

    private static async Task<PostgreSqlContainer> OpenAtThePreviousVersionAsync()
    {
        var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        return database;
    }

    private static async Task MigrateAndBackfillAgainAsync(PostgreSqlContainer database)
    {
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync(Ct);
        await ExecuteAsync(database, AddPriceAdjustment.BackfillSql);
        await ExecuteAsync(database, AddPriceAdjustment.BackfillSql);
    }

    private static ServiceContractsDbContext CreateContext(PostgreSqlContainer database) =>
        new(new DbContextOptionsBuilder<ServiceContractsDbContext>()
            .UseNpgsql(
                database.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ServiceContractsDbContext.Schema))
            .Options);

    private static async Task<Guid> InsertServiceAsync(PostgreSqlContainer database, DateOnly firstDueDate)
    {
        var id = Guid.CreateVersion7();
        var anchor = firstDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await ExecuteAsync(
            database,
            $"""
            INSERT INTO billing.services
                (id, tenant_id, customer_id, name, price, currency, cycle_months, first_due_date, status,
                 last_generated_due_date, version, created_at, status_changed_at, updated_at)
            VALUES ('{id}', gen_random_uuid(), gen_random_uuid(), 'Hosting', 100, 'COP', 1, '{anchor}', 'Active',
                 '{anchor}', 1, now(), now(), now())
            """);
        return id;
    }

    private static Task InsertChargeAsync(PostgreSqlContainer database, Guid serviceId, DateOnly dueDate) => ExecuteAsync(
        database,
        $"""
        INSERT INTO billing.charges (id, tenant_id, service_id, customer_id, amount, currency, due_date, status, version, created_at)
        VALUES (gen_random_uuid(), gen_random_uuid(), '{serviceId}', gen_random_uuid(), 100, 'COP',
            '{dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', 'Pending', 1, now())
        """);

    private static async Task<(string Mode, decimal Points, DateOnly NextReviewDate, DateOnly? HeldSince)> ReadServiceAsync(
        PostgreSqlContainer database, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT adjustment_mode, ipc_extra_points, next_review_date, held_since FROM billing.services WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (
            reader.GetString(0),
            reader.GetDecimal(1),
            reader.GetFieldValue<DateOnly>(2),
            await reader.IsDBNullAsync(3, Ct) ? null : reader.GetFieldValue<DateOnly>(3));
    }

    private static async Task ExecuteAsync(PostgreSqlContainer database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(PostgreSqlContainer database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
