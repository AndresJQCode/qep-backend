using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-11-10 §3.2 (A15): <c>company_id</c> nace <c>NULL</c> en todas las filas existentes, sin respaldo por
/// nombre, y los índices del listado y de la búsqueda de empresas existen.</summary>
public sealed class CustomerCompanyMigrationTests
{
    private const string LastMigrationBeforeCompany = "20261110002205_RelaxCustomerCompleteFields";
    private const string ExistingCustomerId = "01900000-0000-7000-8000-00000000f001";

    private static async Task MigrateGeographyToLatestAsync(string connectionString)
    {
        await using var geography = new GeographyDbContext(
            new DbContextOptionsBuilder<GeographyDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "geography"))
                .Options);
        await geography.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static CustomersDbContext NewCustomersContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "customers"))
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

    [Fact]
    public async Task TheMigrationAddsANullableCompanyColumn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeCompany, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, $"""
            INSERT INTO customers.customers (id, tenant_id, name, business_name, is_active, completeness, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES ('{ExistingCustomerId}', '{TenantId}', 'Laura', 'Laura Pérez S.A.S.', true, 'Incomplete', false, false, 1, now(), now());
            """);

        try
        {
            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

            Assert.Equal("YES", await ScalarAsync<string>(connectionString,
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'customers' AND table_name = 'customers' AND column_name = 'company_id'"));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString,
                $"SELECT count(*) FROM customers.customers WHERE id = '{ExistingCustomerId}' AND company_id IS NULL"));
            Assert.Equal("SET NULL", await ScalarAsync<string>(connectionString,
                "SELECT delete_rule FROM information_schema.referential_constraints WHERE constraint_schema = 'customers' AND constraint_name = 'FK_customers_customers_company_id'"));
        }
        finally
        {
            // El modelo de hoy pide las columnas de las migraciones posteriores: una prueba que baja de versión termina en la última.
            await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TheIndexesExist()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);

        var company = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'customers' AND indexname = 'IX_customers_tenant_company'");
        var trigram = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'customers' AND indexname = 'IX_customers_business_name_trgm'");

        Assert.Contains("(tenant_id, company_id)", company);
        Assert.Contains("WHERE (company_id IS NOT NULL)", company);
        Assert.Contains("gin", trigram);
        Assert.Contains("gin_trgm_ops", trigram);
    }
}
