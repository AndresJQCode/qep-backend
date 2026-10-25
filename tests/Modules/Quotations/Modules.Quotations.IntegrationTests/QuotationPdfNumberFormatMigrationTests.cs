using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración de <c>number_format</c> (spec 2026-10-23, D-R3), contra un PDF guardado antes de que
/// existiera la columna. Migra sólo Quotations y hasta una migración puntual con <see cref="IMigrator"/>,
/// como <see cref="FreightMigrationTests"/>: el host de pruebas migra todo a la última al arrancar y así
/// no habría esquema anterior donde sembrar. Sin relleno, el PDF viejo debe seguir valiendo para un
/// tenant colombiano —que es con lo que se imprimió— y vencerse sólo si el tenant cambió de formato.
/// </summary>
public sealed class QuotationPdfNumberFormatMigrationTests
{
    private const string QuotationId = "01900000-0000-7000-8000-00000000e001";
    private const string TenantId = "01900000-0000-7000-8000-00000000e002";

    // FK apagada: el PDF no necesita su cotización para esta prueba.
    private const string LegacyPdfSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.quotation_pdfs (quotation_id, tenant_id, storage_key, quotation_version, logo_file_id, generated_at)
        VALUES ('{QuotationId}', '{TenantId}', 'quotations/tenants/x/a.pdf', 4, NULL, '2026-10-20T15:00:00Z');
        RESET session_replication_role;
        """;

    [Fact]
    public async Task ALegacyRowKeepsItsCacheForACommaDecimalTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith("_AddQuotationPdfNumberFormat", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(target) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyPdfSql);
        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM quotations.quotation_pdfs WHERE quotation_id = '{QuotationId}' " +
            "AND number_format IS NULL AND storage_key = 'quotations/tenants/x/a.pdf' AND quotation_version = 4"));

        var pdf = await context.Set<QuotationPdf>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.False(pdf.IsStaleFor(4, null, TenantNumberFormats.CommaDecimal));
        Assert.True(pdf.IsStaleFor(4, null, TenantNumberFormats.DotDecimal));
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
