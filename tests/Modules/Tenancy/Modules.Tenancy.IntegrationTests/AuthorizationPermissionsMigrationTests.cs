using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Authorization.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La migración de datos de los permisos de pedidos (spec 2026-09-14). Los roles de fábrica viven
/// en código; los custom guardan sus códigos en <c>authorization.roles.permissions</c> (text[]), y
/// sin esto perderían el acceso sin error. Migra sólo Authorization, hasta una migración puntual,
/// con <see cref="IMigrator"/>: el host de pruebas migra todo a la última al arrancar.
/// </summary>
public sealed class AuthorizationPermissionsMigrationTests
{
    private const string InitialAuthorization = "20260828234451_InitialAuthorization";
    private const string RoleId = "01900000-0000-7000-8000-00000000d001";

    private const string LegacyRoleSql = $"""
        INSERT INTO "authorization".roles (
            id, tenant_id, key, display_name, description, version, created_at, updated_at, permissions)
        VALUES (
            '{RoleId}', '01900000-0000-7000-8000-00000000d002', 'facturacion-junior', 'Facturación junior', '',
            1, now(), now(),
            ARRAY['catalog.product.read', 'quotations.sale.read', 'quotations.sale.manage', 'reporting.sales.read']);
        """;

    [Fact]
    public async Task ACustomRoleWithTheOldCodesEndsWithTheOrderCodes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRoleSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_RenamePermissionsToOrders"), TestContext.Current.CancellationToken);

        // array_replace conserva la posición: el orden de la lista no cambia.
        Assert.Equal(
            ["catalog.product.read", "quotations.order.read", "quotations.order.manage", "reporting.orders.read"],
            await PermissionsAsync(connectionString));
    }

    [Fact]
    public async Task RevertingPutsTheOldCodesBack()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRoleSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_RenamePermissionsToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["catalog.product.read", "quotations.sale.read", "quotations.sale.manage", "reporting.sales.read"],
            await PermissionsAsync(connectionString));
    }

    private static AuthorizationDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "authorization"))
            .Options);

    private static string MigrationId(AuthorizationDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> PermissionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"""SELECT permissions FROM "authorization".roles WHERE id = '{RoleId}'""", connection);
        return (string[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }
}
