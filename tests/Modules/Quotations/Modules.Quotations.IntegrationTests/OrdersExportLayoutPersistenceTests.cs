using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El layout de columnas contra Postgres (spec 2026-09-24, "Persistencia"): la ida y vuelta por la
/// jsonb en orden, el Replace sobre la entidad rastreada sin método Update, el choque de PK de dos
/// primeros guardados traducido a 412 (D9), y la migración que aplica y revierte.
/// </summary>
public sealed class OrdersExportLayoutPersistenceTests
{
    private const string LastMigrationBeforeTheLayout = "20260923152513_AddQuotationIsRetail";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    private const string ColumnsSql = """
        SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns
        WHERE table_schema = 'quotations' AND table_name = 'orders_export_layouts'
        """;

    private const string PrimaryKeySql = """
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'quotations' AND t.relname = 'orders_export_layouts' AND c.contype = 'p'
        """;

    [Fact]
    public async Task TheLayoutRoundTripsThroughTheJsonColumnInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>();
            Assert.Null(await repository.FindAsync(tenantId, TestContext.Current.CancellationToken));

            var layout = OrdersExportLayout.CreateDefault(tenantId, Now);
            Assert.True(layout.Replace(
                [
                    OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
                    OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
                    OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
                ],
                Now));
            repository.Add(layout);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reloaded = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);

            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Version);
            Assert.Equal(Now, reloaded.UpdatedAt);
            Assert.Equal(34, reloaded.Columns.Count);
            Assert.Equal(OrdersExportColumnKind.Fixed, reloaded.Columns[0].Kind);
            Assert.Equal("Tipo Doc", reloaded.Columns[0].Header);
            Assert.Equal("FV", reloaded.Columns[0].Value);
            Assert.Null(reloaded.Columns[0].Key);
            Assert.Equal("email", reloaded.Columns[1].Key);
            Assert.Equal("Correo", reloaded.Columns[1].Header);
            Assert.Null(reloaded.Columns[1].Value);
            Assert.False(reloaded.Columns[2].Visible);
            Assert.Equal("product_code", reloaded.Columns[3].Key);
        }

        // La fila se lee a mano: kind como texto y los nombres del JSON en minúsculas.
        var connectionString = database.GetConnectionString();
        Assert.Equal("Fixed", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 0 ->> 'kind' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("Correo", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 1 ->> 'header' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("false", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 2 ->> 'visible' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
    }

    // Sin Update en el puerto: FindAsync devuelve la entidad rastreada y SaveChangesAsync persiste
    // el Replace, como en el resto de los repositorios del módulo.
    [Fact]
    public async Task ReplacingATrackedLayoutPersistsThroughSaveChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;
        await SaveFirstLayoutAsync(factory, tenantId, OrdersExportColumnSetting.Catalog("email", "Correo", visible: true));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var layout = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(layout);
            Assert.True(layout.Replace([OrdersExportColumnSetting.Catalog("city", "Municipio", visible: true)], Now.AddMinutes(1)));
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reloaded = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.Version);
            Assert.Equal("city", reloaded.Columns[0].Key);
            Assert.Equal("Municipio", reloaded.Columns[0].Header);
            Assert.Equal("Email", reloaded.Columns.Single(column => column.Key == "email").Header);
        }
    }

    // D9: dos primeros PUT simultáneos pasan el chequeo de versión en memoria (los dos vieron "1")
    // y los dos intentan INSERT. El segundo choca en la PK, y eso es un 412 —alguien guardó
    // primero—, no un 500 con el nombre de la constraint adentro.
    [Fact]
    public async Task TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstLayout = await LoadOrDefaultAsync(first, tenantId);
        var secondLayout = await LoadOrDefaultAsync(second, tenantId);
        Assert.True(firstLayout.Replace([OrdersExportColumnSetting.Catalog("email", "Correo", visible: true)], Now));
        Assert.True(secondLayout.Replace([OrdersExportColumnSetting.Catalog("city", "Municipio", visible: true)], Now));
        first.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(firstLayout);
        second.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(secondLayout);
        await first.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal("Correo", await ScalarAsync<string>(
            database.GetConnectionString(),
            $"SELECT columns -> 0 ->> 'header' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task TheMigrationCreatesTheTableAndRevertingDropsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheLayout, TestContext.Current.CancellationToken);
        Assert.Empty(await ListAsync(connectionString, ColumnsSql));

        await migrator.MigrateAsync(MigrationId(context, "_AddOrdersExportLayout"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["columns:jsonb:NO", "tenant_id:uuid:NO", "updated_at:timestamp with time zone:NO", "version:bigint:NO"],
            await ListAsync(connectionString, ColumnsSql));
        Assert.Equal(["PK_orders_export_layouts"], await ListAsync(connectionString, PrimaryKeySql));

        await migrator.MigrateAsync(LastMigrationBeforeTheLayout, TestContext.Current.CancellationToken);

        Assert.Empty(await ListAsync(connectionString, ColumnsSql));
    }

    private static async Task SaveFirstLayoutAsync(
        QepApiFactory factory, Guid tenantId, params OrdersExportColumnSetting[] columns)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var layout = OrdersExportLayout.CreateDefault(tenantId, Now);
        Assert.True(layout.Replace(columns, Now));
        scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(layout);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Lo mismo que hará el handler: la fila, o el por defecto en memoria (D9).
    private static async Task<OrdersExportLayout> LoadOrDefaultAsync(AsyncServiceScope scope, Guid tenantId) =>
        await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
            .FindAsync(tenantId, TestContext.Current.CancellationToken)
        ?? OrdersExportLayout.CreateDefault(tenantId, Now);

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(QuotationsDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Ordenado en C# y no con ORDER BY: el orden de texto de Postgres depende de la
    /// collation de la base.</summary>
    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }
}
