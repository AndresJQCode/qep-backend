using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Variantes, Tarea 6: el SQL de <see cref="CatalogVariantUsageChecker"/> contra PostgreSQL.
///
/// **Las tablas de líneas se crean acá, mínimas, y no por las migraciones.** La columna
/// <c>variant_id</c> de <c>quotations.quotation_items</c>, <c>quotations.order_items</c> y
/// <c>pos.sale_lines</c> la agregan las Tareas 10 y 12 (<c>AddVariantToLines</c>,
/// <c>AddVariantToSaleLines</c>). La réplica lleva sólo las columnas que la consulta lee —el
/// documento dueño con su <c>tenant_id</c> y la línea con su <c>variant_id</c>— con los mismos
/// nombres de esquema, tabla y columna. Esta prueba no se entera si una migración nombra distinto
/// una columna: eso lo cubren las gemelas contra el esquema migrado,
/// <c>VariantLinesMigrationTests</c> (Quotations) y <c>SaleLinesVariantMigrationTests</c> (Pos).
/// </summary>
public sealed class CatalogVariantUsageCheckerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    // Una variante por tabla de líneas, una sin líneas y una vendida sólo en otro tenant: vuelven
    // las tres primeras, cada una una vez aunque tenga dos líneas.
    [Fact]
    public async Task CheckerFindsVariantUsedByQuotationOrderAndSaleLine()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var quoted = Guid.CreateVersion7();
        var ordered = Guid.CreateVersion7();
        var sold = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        var otherTenants = Guid.CreateVersion7();

        await AddQuotationLineAsync(database, TenantId, quoted);
        await AddQuotationLineAsync(database, TenantId, quoted);
        await AddOrderLineAsync(database, TenantId, ordered);
        await AddSaleLineAsync(database, TenantId, sold);
        await AddQuotationLineAsync(database, OtherTenantId, otherTenants);
        await AddSaleLineAsync(database, OtherTenantId, otherTenants);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [quoted, ordered, sold, unused, otherTenants], TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<Guid> { quoted, ordered, sold }, used.ToHashSet());
    }

    // Sin ids no consulta: ni siquiera hacen falta las tablas.
    [Fact]
    public async Task NoVariantsGiveNothingWithoutQuerying()
    {
        await using var database = await StartDatabaseAsync();

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [], TestContext.Current.CancellationToken);

        Assert.Empty(used);
    }

    // La consulta corre en la conexión del CatalogDbContext: con una transacción abierta ve lo que
    // esa transacción escribió y todavía no confirmó. Es lo que le permite al PUT repetir la
    // comprobación dentro de su transacción (Enfoque de revisión 3).
    [Fact]
    public async Task TheCheckRunsInsideTheCatalogTransaction()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);
        var variant = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();

        await using var catalog = CatalogFor(database);
        await using var transaction = await catalog.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await catalog.Database.ExecuteSqlAsync(
            $"INSERT INTO pos.sales (id, tenant_id) VALUES ({saleId}, {TenantId})",
            TestContext.Current.CancellationToken);
        await catalog.Database.ExecuteSqlAsync(
            $"INSERT INTO pos.sale_lines (sale_id, variant_id) VALUES ({saleId}, {variant})",
            TestContext.Current.CancellationToken);

        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [variant], TestContext.Current.CancellationToken);

        Assert.Contains(variant, used);
    }

    private static CatalogDbContext CatalogFor(PostgreSqlContainer database) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options);

    private static async Task CreateLineTablesAsync(PostgreSqlContainer database) =>
        await ExecuteAsync(database, """
            CREATE SCHEMA quotations;
            CREATE SCHEMA pos;
            CREATE TABLE quotations.quotations (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);
            CREATE TABLE quotations.quotation_items (quotation_id uuid NOT NULL REFERENCES quotations.quotations (id), variant_id uuid NOT NULL);
            CREATE TABLE quotations.orders (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);
            CREATE TABLE quotations.order_items (order_id uuid NOT NULL REFERENCES quotations.orders (id), variant_id uuid NOT NULL);
            CREATE TABLE pos.sales (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);
            CREATE TABLE pos.sale_lines (sale_id uuid NOT NULL REFERENCES pos.sales (id), variant_id uuid NOT NULL);
            """);

    private static Task AddQuotationLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "quotations.quotations", "quotations.quotation_items", "quotation_id", tenantId, variantId);

    private static Task AddOrderLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "quotations.orders", "quotations.order_items", "order_id", tenantId, variantId);

    private static Task AddSaleLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "pos.sales", "pos.sale_lines", "sale_id", tenantId, variantId);

    // Los nombres de tabla son constantes de esta clase, nunca entrada: por eso van interpolados.
    private static async Task AddLineAsync(
        PostgreSqlContainer database, string documents, string lines, string documentColumn, Guid tenantId, Guid variantId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {documents} (id, tenant_id) VALUES (@document, @tenant);
            INSERT INTO {lines} ({documentColumn}, variant_id) VALUES (@document, @variant);
            """,
            connection);
        command.Parameters.AddWithValue("document", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("variant", variantId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(PostgreSqlContainer database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
