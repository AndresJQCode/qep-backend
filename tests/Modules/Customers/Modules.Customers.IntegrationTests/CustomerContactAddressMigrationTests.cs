using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Persistence;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La migración que devuelve el domicilio al cliente (spec 2026-09-18), contra una base con un
/// cliente y dos direcciones del esquema anterior. Migra sólo Customers, y hasta una migración
/// puntual, con <see cref="IMigrator"/>: el host de pruebas migra todo a la última al arrancar, y
/// así no habría esquema viejo donde sembrar. Mismo mecanismo que <c>OrdersMigrationTests</c>.
///
/// Geography sí se migra a la última antes: dos migraciones de Customers declaran FK hacia
/// <c>geography.cities</c>, y la FK nueva se valida al crearse, así que las ciudades tienen que
/// existir de verdad. <c>GeographySeeder</c> no corre sin el host, por eso van por SQL.
/// </summary>
public sealed class CustomerContactAddressMigrationTests
{
    private const string LastMigrationBeforeTheContactAddress = "20260906150110_AddCustomerBusinessName";

    private const string DepartmentAId = "01900000-0000-7000-8000-00000000d001";
    private const string DepartmentBId = "01900000-0000-7000-8000-00000000d002";
    private const string CityAId = "01900000-0000-7000-8000-00000000d003";
    private const string CityBId = "01900000-0000-7000-8000-00000000d004";
    private const string ClassificationId = "01900000-0000-7000-8000-00000000d005";
    private const string CustomerId = "01900000-0000-7000-8000-00000000d006";
    private const string AddressAId = "01900000-0000-7000-8000-00000000d007";
    private const string AddressBId = "01900000-0000-7000-8000-00000000d008";

    private const string GeographySql = $"""
        INSERT INTO geography.departments (id, divipola_code, name) VALUES
            ('{DepartmentAId}', '05', 'Antioquia'),
            ('{DepartmentBId}', '11', 'Bogotá, D.C.');
        INSERT INTO geography.cities (id, divipola_code, name, department_id) VALUES
            ('{CityAId}', '05001', 'Medellín', '{DepartmentAId}'),
            ('{CityBId}', '11001', 'Bogotá, D.C.', '{DepartmentBId}');
        """;

    // Un cliente del esquema anterior a la migración: sin address ni city_id propios. La
    // principal está en la ciudad B y la otra en la A, para que el backfill tenga que elegir.
    private const string CustomerSql = $"""
        INSERT INTO customers.client_classifications (
            id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES ('{ClassificationId}', '{TenantId}', 'Mediano', 'CLI', true, 1,
                '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number,
            is_active, phone, email, classification_id, with_retention, vat_surplus, version,
            created_at, updated_at)
        VALUES ('{CustomerId}', '{TenantId}', 'CLI05000001', 'Verde Esencial S.A.S.', NULL, 'Nit',
                '900.123.456-1', true, '310 935 2187', 'compras@verde.co', '{ClassificationId}',
                false, false, 1, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    private const string TwoAddressesWithPrincipalInCityBSql = $"""
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        VALUES
            ('{AddressAId}', '{CustomerId}', 'Bodega Norte', 'Calle 10 # 45-12', NULL, '{CityAId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z'),
            ('{AddressBId}', '{CustomerId}', 'Oficina', 'Carrera 7 # 71-21', NULL, '{CityBId}',
             true, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    // El dato roto del spec (§ Bordes): un cliente sin principal. No debería existir, pero si
    // existe la migración tiene que denunciarlo, no inventarle un domicilio.
    private const string TwoAddressesWithoutPrincipalSql = $"""
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        VALUES
            ('{AddressAId}', '{CustomerId}', 'Bodega Norte', 'Calle 10 # 45-12', NULL, '{CityAId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z'),
            ('{AddressBId}', '{CustomerId}', 'Oficina', 'Carrera 7 # 71-21', NULL, '{CityBId}',
             false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z');
        """;

    private const string ContactColumnsCountSql = """
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'customers' AND table_name = 'customers'
          AND column_name IN ('address', 'city_id')
        """;

    [Fact]
    public async Task TheBackfillCopiesThePrincipalAddressIntoTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithPrincipalInCityBSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Parse(CityBId), await ScalarAsync<Guid>(
            connectionString, $"SELECT city_id FROM customers.customers WHERE id = '{CustomerId}'"));
        Assert.Equal("Carrera 7 # 71-21", await ScalarAsync<string>(
            connectionString, $"SELECT address FROM customers.customers WHERE id = '{CustomerId}'"));
        // La libreta no se toca: el backfill copia, no mueve.
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM customers.customer_addresses WHERE customer_id = '{CustomerId}'"));
        Assert.Equal("NO", await ScalarAsync<string>(
            connectionString,
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'customers' "
            + "AND table_name = 'customers' AND column_name = 'city_id'"));
        // El DEFAULT '' solo servia para crear la columna; en el esquema final no queda.
        Assert.True(await ScalarAsync<bool>(
            connectionString,
            "SELECT column_default IS NULL FROM information_schema.columns WHERE table_schema = 'customers' "
            + "AND table_name = 'customers' AND column_name = 'address'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_customers_cities_city_id'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'customers' AND indexname = 'IX_customers_city'"));
    }

    [Fact]
    public async Task ACustomerWithoutAPrincipalAddressStopsTheMigration()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithoutPrincipalSql);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken));

        // 23502 = not_null_violation: el SET NOT NULL del paso 3 encontro el city_id nulo que dejo
        // el backfill. La transaccion deshace los pasos anteriores: no quedan columnas a medias.
        Assert.Equal("23502", exception.SqlState);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, ContactColumnsCountSql));
    }

    [Fact]
    public async Task RevertingRemovesTheColumnsAndLeavesTheAddressBookIntact()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await MigrateGeographyToLatestAsync(connectionString);
        await using var context = NewCustomersContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, GeographySql + CustomerSql + TwoAddressesWithPrincipalInCityBSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_AddCustomerContactAddress"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(LastMigrationBeforeTheContactAddress, TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, ContactColumnsCountSql));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM customers.customer_addresses WHERE customer_id = '{CustomerId}'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_customers_cities_city_id'"));
    }

    // Geography entera: Customers referencia geography.cities desde AddCustomerCityAndClassification.
    private static async Task MigrateGeographyToLatestAsync(string connectionString)
    {
        await using var geography = new GeographyDbContext(
            new DbContextOptionsBuilder<GeographyDbContext>()
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "geography"))
                .Options);
        await geography.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static CustomersDbContext NewCustomersContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "customers"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
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
