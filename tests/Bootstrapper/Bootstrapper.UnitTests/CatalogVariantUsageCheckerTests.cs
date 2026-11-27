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

    // WMS-D: una variante con movimientos o saldos en el ledger no se borra (spec 2026-10-31 §17), aunque ninguna línea la venda.
    [Fact]
    public async Task CheckerFindsVariantUsedOnlyByAWmsMovementOrBalance()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var moved = Guid.CreateVersion7();
        var balanced = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        var otherTenantMoved = Guid.CreateVersion7();
        var otherTenantBalanced = Guid.CreateVersion7();

        await ExecuteAsync(database, $"""
            INSERT INTO wms.stock_movements (tenant_id, variant_id) VALUES ('{TenantId}', '{moved}'), ('{OtherTenantId}', '{otherTenantMoved}');
            INSERT INTO wms.stock_balances (tenant_id, variant_id) VALUES ('{TenantId}', '{balanced}'), ('{OtherTenantId}', '{otherTenantBalanced}');
            """);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [moved, balanced, unused, otherTenantMoved, otherTenantBalanced], TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<Guid> { moved, balanced }, used.ToHashSet());
    }

    // Un ajuste abierto (Proposed o Approved) todavía puede contabilizarse y escribiría un movimiento inmutable sobre la variante: no se
    // borra aunque aún no tenga movimientos. Uno Posted ya dejó su movimiento (lo cubre stock_movements) y uno Rejected no escribe nada.
    // Un lote también ata a la variante.
    [Fact]
    public async Task CheckerFindsVariantUsedOnlyByAnOpenAdjustmentLineOrALot()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var proposed = Guid.CreateVersion7();
        var approved = Guid.CreateVersion7();
        var posted = Guid.CreateVersion7();
        var rejected = Guid.CreateVersion7();
        var lotted = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();
        var otherTenantProposed = Guid.CreateVersion7();
        var otherTenantLotted = Guid.CreateVersion7();

        await AddAdjustmentLineAsync(database, TenantId, "Proposed", proposed);
        await AddAdjustmentLineAsync(database, TenantId, "Approved", approved);
        await AddAdjustmentLineAsync(database, TenantId, "Posted", posted);
        await AddAdjustmentLineAsync(database, TenantId, "Rejected", rejected);
        await AddAdjustmentLineAsync(database, OtherTenantId, "Proposed", otherTenantProposed);
        await ExecuteAsync(database, $"""
            INSERT INTO wms.lots (tenant_id, variant_id) VALUES ('{TenantId}', '{lotted}'), ('{OtherTenantId}', '{otherTenantLotted}');
            """);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId,
            [proposed, approved, posted, rejected, lotted, unused, otherTenantProposed, otherTenantLotted],
            TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<Guid> { proposed, approved, lotted }, used.ToHashSet());
    }

    // Una línea abierta de una tarea sin terminar todavía va a escribir en el ledger (spec 2026-11-26, DA-37). Una línea cerrada ya
    // dejó su movimiento o no escribirá ninguno, y una tarea terminada no tiene líneas abiertas.
    [Fact]
    public async Task CheckerFindsVariantUsedOnlyByAnOpenTaskLine()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var planned = Guid.CreateVersion7();
        var ready = Guid.CreateVersion7();
        var assigned = Guid.CreateVersion7();
        var inProgress = Guid.CreateVersion7();
        var done = Guid.CreateVersion7();
        var cancelled = Guid.CreateVersion7();
        var closedLine = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();

        await AddTaskLineAsync(database, TenantId, "Planned", "Open", planned);
        await AddTaskLineAsync(database, TenantId, "Ready", "Open", ready);
        await AddTaskLineAsync(database, TenantId, "Assigned", "Open", assigned);
        await AddTaskLineAsync(database, TenantId, "InProgress", "Open", inProgress);
        await AddTaskLineAsync(database, TenantId, "Done", "Done", done);
        await AddTaskLineAsync(database, TenantId, "Cancelled", "Cancelled", cancelled);
        await AddTaskLineAsync(database, TenantId, "InProgress", "Cancelled", closedLine);
        await AddTaskLineAsync(database, OtherTenantId, "Ready", "Open", otherTenant);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId,
            [planned, ready, assigned, inProgress, done, cancelled, closedLine, otherTenant],
            TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<Guid> { planned, ready, assigned, inProgress }, used.ToHashSet());
    }

    // DA-51: una novedad abierta (Open o InReview) todavía puede escribir en el ledger al resolverse (un SyncConflict aceptado en el
    // stock), así que la variante no se borra. Una resuelta o rechazada ya no escribe nada.
    [Fact]
    public async Task AnOpenIncidentWithTheVariantCountsAsUse()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var open = Guid.CreateVersion7();
        var inReview = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        await AddIncidentAsync(database, TenantId, "Open", open);
        await AddIncidentAsync(database, TenantId, "InReview", inReview);
        await AddIncidentAsync(database, OtherTenantId, "Open", otherTenant);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [open, inReview, otherTenant], TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<Guid> { open, inReview }, used.ToHashSet());
    }

    [Fact]
    public async Task AResolvedOrRejectedIncidentDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        await CreateLineTablesAsync(database);

        var resolved = Guid.CreateVersion7();
        var rejected = Guid.CreateVersion7();
        await AddIncidentAsync(database, TenantId, "Resolved", resolved);
        await AddIncidentAsync(database, TenantId, "Rejected", rejected);

        await using var catalog = CatalogFor(database);
        var used = await new CatalogVariantUsageChecker(catalog).FindUsedAsync(
            TenantId, [resolved, rejected], TestContext.Current.CancellationToken);

        Assert.Empty(used);
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
            CREATE SCHEMA wms;
            CREATE TABLE wms.stock_movements (tenant_id uuid NOT NULL, variant_id uuid NOT NULL);
            CREATE TABLE wms.stock_balances (tenant_id uuid NOT NULL, variant_id uuid NOT NULL);
            CREATE TABLE wms.lots (tenant_id uuid NOT NULL, variant_id uuid NOT NULL);
            CREATE TABLE wms.adjustments (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, status text NOT NULL);
            CREATE TABLE wms.adjustment_lines (adjustment_id uuid NOT NULL REFERENCES wms.adjustments (id), variant_id uuid NOT NULL);
            CREATE TABLE wms.tasks (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, status text NOT NULL);
            CREATE TABLE wms.task_lines (task_id uuid NOT NULL REFERENCES wms.tasks (id), variant_id uuid NOT NULL, line_status text NOT NULL);
            CREATE TABLE wms.incidents (tenant_id uuid NOT NULL, variant_id uuid, status text NOT NULL);
            """);

    private static Task AddIncidentAsync(PostgreSqlContainer database, Guid tenantId, string status, Guid variantId) =>
        ExecuteAsync(database, $"INSERT INTO wms.incidents (tenant_id, variant_id, status) VALUES ('{tenantId}', '{variantId}', '{status}');");

    private static async Task AddTaskLineAsync(
        PostgreSqlContainer database, Guid tenantId, string status, string lineStatus, Guid variantId)
    {
        var task = Guid.CreateVersion7();
        await ExecuteAsync(database, $"""
            INSERT INTO wms.tasks (id, tenant_id, status) VALUES ('{task}', '{tenantId}', '{status}');
            INSERT INTO wms.task_lines (task_id, variant_id, line_status) VALUES ('{task}', '{variantId}', '{lineStatus}');
            """);
    }

    private static Task AddQuotationLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "quotations.quotations", "quotations.quotation_items", "quotation_id", tenantId, variantId);

    private static Task AddOrderLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "quotations.orders", "quotations.order_items", "order_id", tenantId, variantId);

    private static Task AddSaleLineAsync(PostgreSqlContainer database, Guid tenantId, Guid variantId) =>
        AddLineAsync(database, "pos.sales", "pos.sale_lines", "sale_id", tenantId, variantId);

    private static async Task AddAdjustmentLineAsync(PostgreSqlContainer database, Guid tenantId, string status, Guid variantId)
    {
        var adjustment = Guid.CreateVersion7();
        await ExecuteAsync(database, $"""
            INSERT INTO wms.adjustments (id, tenant_id, status) VALUES ('{adjustment}', '{tenantId}', '{status}');
            INSERT INTO wms.adjustment_lines (adjustment_id, variant_id) VALUES ('{adjustment}', '{variantId}');
            """);
    }

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
