using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §7.2: la migración sobre clientes viejos (sin teléfono ni correo) los deja
/// <c>Complete</c>; el <c>CHECK</c> rechaza un completo sin CUC y acepta un incompleto sin nada; el
/// <c>Down</c> borra los incompletos antes de volver a exigir las columnas.</summary>
public sealed class CustomerCompletenessMigrationTests
{
    private const string LastMigrationBeforeCompleteness = "20261010000733_AddCustomerPhoneE164";
    private const string ClassificationId = "01900000-0000-7000-8000-00000000e001";
    private const string OldCustomerId = "01900000-0000-7000-8000-00000000e002";
    private const string IncompleteCustomerId = "01900000-0000-7000-8000-00000000e003";
    private const string SecondIncompleteCustomerId = "01900000-0000-7000-8000-00000000e004";

    // Un cliente de afuera (sin ciudad DIVIPOLA) sin teléfono ni correo: lo que el CHECK no puede tumbar.
    // Después de la migración, completeness no tiene DEFAULT: hay que nombrarla (afterCompleteness).
    private static string OldCustomerSql(bool afterCompleteness = false) => $"""
        INSERT INTO customers.client_classifications (id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES ('{ClassificationId}', '{TenantId}', 'Mediano', 'CLI', true, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number, is_active, phone, email,
            address, country, city_id, city_name, phone_e164, classification_id, with_retention, vat_surplus, version, created_at, updated_at
            {(afterCompleteness ? ", completeness" : string.Empty)})
        VALUES ('{OldCustomerId}', '{TenantId}', 'CLI00000001', 'Verde Esencial S.L.', NULL, 'Nit', 'B-12345678', true, NULL, NULL,
                'Calle Gran Via 28', 'ES', NULL, 'Madrid', NULL, '{ClassificationId}', false, false, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z'
                {(afterCompleteness ? ", 'Complete'" : string.Empty)});
        """;

    private static string IncompleteCustomerSql(string id) => $"""
        INSERT INTO customers.customers (id, tenant_id, name, is_active, phone, phone_e164, completeness, whatsapp_user_id,
                                         with_retention, vat_surplus, version, created_at, updated_at)
        VALUES ('{id}', '{TenantId}', 'Laura', true, '+573001234567', '+573001234567', 'Incomplete', 'CO.1349120865530274',
                false, false, 1, '2026-10-10T12:00:00Z', '2026-10-10T12:00:00Z');
        """;

    [Fact]
    public async Task OldCustomersBecomeCompleteAndTheChecksHoldTheLine()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeCompleteness, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, OldCustomerSql());

        await migrator.MigrateAsync(MigrationId(context, "_AddCustomerCompleteness"), TestContext.Current.CancellationToken);

        Assert.Equal("Complete|", await ScalarAsync<string>(connectionString,
            $"SELECT completeness || '|' || coalesce(whatsapp_user_id, '') FROM customers.customers WHERE id = '{OldCustomerId}'"));
        // El DEFAULT sólo sirvió para llenar las filas viejas.
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT column_default IS NULL FROM information_schema.columns WHERE table_schema = 'customers' AND table_name = 'customers' AND column_name = 'completeness'"));
        // Sin classification_id en el INSERT: si quedara el DEFAULT Guid.Empty de AddCustomerCityAndClassification,
        // moriría por FK en vez de entrar como incompleto.
        await ExecuteAsync(connectionString, IncompleteCustomerSql(IncompleteCustomerId));
        var withoutCuc = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, $"UPDATE customers.customers SET cuc = NULL WHERE id = '{OldCustomerId}'"));
        Assert.Equal("23514", withoutCuc.SqlState);
        Assert.Equal("CK_customers_complete_fields", withoutCuc.ConstraintName);
        var badValue = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, $"UPDATE customers.customers SET completeness = 'Pending' WHERE id = '{OldCustomerId}'"));
        Assert.Equal("CK_customers_completeness", badValue.ConstraintName);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, IncompleteCustomerSql(SecondIncompleteCustomerId)));
        Assert.Equal("IX_customers_tenant_whatsapp_user_id", duplicate.ConstraintName);
    }

    [Fact]
    public async Task RevertingDeletesTheIncompleteCustomersFirst()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_AddCustomerCompleteness"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, OldCustomerSql(afterCompleteness: true) + IncompleteCustomerSql(IncompleteCustomerId));

        await migrator.MigrateAsync(LastMigrationBeforeCompleteness, TestContext.Current.CancellationToken);

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM customers.customers"));
        Assert.Equal("NO", await ScalarAsync<string>(connectionString,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'customers' AND table_name = 'customers' AND column_name = 'cuc'"));
    }

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

    private static string MigrationId(CustomersDbContext context, string suffix) =>
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
}
