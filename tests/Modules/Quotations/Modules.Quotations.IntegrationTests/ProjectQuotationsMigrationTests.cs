using BuildingBlocks.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// <c>AddProjectQuotations</c> (spec 2026-11-26 §5): las columnas son aditivas —lo que ya existe, y lo que inserte un pod
/// viejo o un <c>INSERT</c> crudo, queda <c>Standard</c>—, los <c>CHECK</c> cierran los dos nombres y el alcance va y vuelve
/// por el <c>jsonb</c>. Migra sólo Quotations sobre una base vacía, como <see cref="OrderDateMigrationTests"/>, y termina en
/// la última migración.
/// </summary>
public sealed class ProjectQuotationsMigrationTests
{
    private const string LastMigrationBeforeProjects = "AddOrdersExportLayoutPhoneDialCode";

    private const string TenantId = "01900000-0000-7000-8000-000000007a01";
    private const string MemberId = "01900000-0000-7000-8000-000000007f01";
    private const string OldQuotation = "01900000-0000-7000-8000-000000007101";
    private const string OldOrder = "01900000-0000-7000-8000-000000007201";
    private const string NewQuotation = "01900000-0000-7000-8000-000000007102";
    private const string NewOrder = "01900000-0000-7000-8000-000000007202";
    private const string CreatedAt = "2026-11-20T15:00:00Z";

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExistingRowsAreStandardAndARawInsertWithoutKindIsStandard()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var target = context.Database.GetMigrations()
            .Single(id => id.EndsWith($"_{LastMigrationBeforeProjects}", StringComparison.Ordinal));
        await migrator.MigrateAsync(target, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, Insert(OldQuotation, OldOrder));

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, Insert(NewQuotation, NewOrder));

        Assert.Equal(
            new[] { $"{OldQuotation}|Standard|-|-", $"{NewQuotation}|Standard|-|-" },
            await ListAsync(
                connectionString,
                "SELECT id::text || '|' || kind || '|' || coalesce(project_scope::text, '-') || '|' "
                + "|| coalesce(project_pricing_mode, '-') FROM quotations.quotations ORDER BY id"));
        Assert.Equal(
            new[] { $"{OldOrder}|Standard|-", $"{NewOrder}|Standard|-" },
            await ListAsync(
                connectionString,
                "SELECT id::text || '|' || kind || '|' || coalesce(project_scope::text, '-') "
                + "FROM quotations.orders ORDER BY id"));
    }

    [Fact]
    public async Task TheChecksRejectAnUnknownKindOrMode()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(connectionString, Insert(OldQuotation, OldOrder));

        await AssertCheckViolationAsync(
            connectionString, "UPDATE quotations.quotations SET kind = 'Other'", "CK_quotations_kind");
        await AssertCheckViolationAsync(
            connectionString,
            "UPDATE quotations.quotations SET project_pricing_mode = 'Hourly'",
            "CK_quotations_project_pricing_mode");
        await AssertCheckViolationAsync(connectionString, "UPDATE quotations.orders SET kind = 'Other'", "CK_orders_kind");
    }

    [Fact]
    public async Task AProjectScopeRoundTripsThroughJsonb()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (quotation, order) = ConvertedProject();
        await using (var context = NewContext(connectionString))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            context.Add(quotation);
            context.Add(order);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reading = NewContext(connectionString);
        var reloaded = await reading.Quotations.SingleAsync(
            value => value.Id == quotation.Id, TestContext.Current.CancellationToken);
        var reloadedOrder = await reading.Orders.SingleAsync(
            value => value.Id == order.Id, TestContext.Current.CancellationToken);

        Assert.Equal(QuotationKind.Project, reloaded.Kind);
        Assert.Equal(quotation.ProjectScope, reloaded.ProjectScope);
        Assert.Equal(1_000_000m, reloaded.ProjectScope!.Total);
        Assert.Equal(ProjectPricingMode.PerPhase, reloaded.ProjectPricingMode);
        Assert.Equal(QuotationKind.Project, reloadedOrder.Kind);
        Assert.Equal(quotation.ProjectScope, reloadedOrder.ProjectScope);
        // Releer no es un cambio: sin comparador por valor, EF reescribiría el jsonb en cada guardado.
        reading.ChangeTracker.DetectChanges();
        Assert.False(reading.ChangeTracker.HasChanges());
        Assert.Equal(
            "Project|PerPhase|PerPhase",
            Assert.Single(await ListAsync(
                connectionString,
                "SELECT kind || '|' || project_pricing_mode || '|' || (project_scope ->> 'pricingMode') "
                + "FROM quotations.quotations")));
        // El total se deriva y no se guarda (spec §5).
        Assert.Equal(
            "globalPrice,items,phases,pricingMode,serviceVariantId",
            Assert.Single(await ListAsync(
                connectionString,
                "SELECT string_agg(key, ',' ORDER BY key) FROM quotations.quotations, "
                + "jsonb_object_keys(project_scope) AS key")));
    }

    // La regla de RequiresShipping en SQL (spec §6.4): un proyecto no se despacha, tampoco sin líneas.
    [Fact]
    public async Task AProjectNeverRequiresShippingInTheListQueries()
    {
        await using var database = await StartEmptyDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (converted, order) = ConvertedProject();
        var empty = Draft(QuotationKind.Project, "COT-2026-0002");
        var standard = Draft(QuotationKind.Standard, "COT-2026-0003");
        var (standardQuotation, standardOrder) = ConvertedStandard();
        await using (var context = NewContext(connectionString))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            context.AddRange(converted, order, empty, standard, standardQuotation, standardOrder);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            // La línea del pedido como física: la regla mira el tipo del documento y no sólo sus líneas.
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE quotations.order_items SET kind = 'Physical'", TestContext.Current.CancellationToken);
        }

        await using var reading = NewContext(connectionString);
        var quotations = await new QuotationRepository(reading).FindIdsRequiringShippingAsync(
            converted.TenantId, [converted.Id, empty.Id, standard.Id], TestContext.Current.CancellationToken);
        var orders = await new OrderRepository(reading).FindIdsRequiringShippingAsync(
            order.TenantId, [order.Id, standardOrder.Id], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { standard.Id.Value }, quotations);
        // El control positivo: la misma consulta sí devuelve el pedido estándar con línea física.
        Assert.Equal(new[] { standardOrder.Id.Value }, orders);
    }

    private static (Quotation Quotation, Order Order) ConvertedStandard()
    {
        var member = new MemberId(Guid.Parse(MemberId));
        var quotation = Draft(QuotationKind.Standard, "COT-2026-0004");
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 100_000m, 0m, 19, member, Now);
        quotation.ConvertToOrder(member, Now, QuotationProductSnapshot.None);
        var order = Order.FromQuotation(
            OrderId.New(), quotation.TenantId, "PED-2026-0002", quotation, null, member, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        return (quotation, order);
    }

    private static Quotation Draft(QuotationKind kind, string number) =>
        Quotation.Create(
            QuotationId.New(), Guid.Parse(TenantId), number, Guid.CreateVersion7(),
            new MemberId(Guid.Parse(MemberId)), new DateOnly(2026, 12, 31), paymentMethod: null, notes: null,
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "7542",
                Currency = "COP",
                Country = "CO",
            },
            "COP", customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None,
            new MemberId(Guid.Parse(MemberId)), Now, kind);

    private static (Quotation Quotation, Order Order) ConvertedProject()
    {
        var member = new MemberId(Guid.Parse(MemberId));
        var quotation = Draft(QuotationKind.Project, "COT-2026-0001");
        var service = new ProjectServiceLine(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 19, ProductKind.Service, SalesUnit.Service);
        var scope = ProjectScope.Create(
            service.VariantId, ProjectPricingMode.PerPhase, null,
            [
                new ProjectPhase(Guid.CreateVersion7(), "Descubrimiento", 600_000m,
                    [new ProjectItem(Guid.CreateVersion7(), "Talleres con el equipo de ventas")]),
                new ProjectPhase(Guid.CreateVersion7(), "Construcción", 400_000m,
                    [new ProjectItem(Guid.CreateVersion7(), "Integración \"a la medida\" — fase 2")]),
            ],
            []);
        quotation.ReplaceProjectScope(scope, service, member, Now);
        quotation.ConvertToOrder(member, Now, QuotationProductSnapshot.None);
        var order = Order.FromQuotation(
            OrderId.New(), quotation.TenantId, "PED-2026-0001", quotation, null, member, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        return (quotation, order);
    }

    // Las columnas obligatorias de las dos tablas antes de AddProjectQuotations, sin kind: lo que escriben un pod viejo y
    // los INSERT crudos del repositorio.
    private static string Insert(string quotationId, string orderId) => $"""
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_vat_surplus, retention_amount, net_total, notes, created_by, updated_by,
            updated_at, version)
        VALUES (
            '{quotationId}', '{TenantId}', 'COT-{quotationId[^5..]}', '{MemberId}', '{MemberId}', 'Draft', '{CreatedAt}',
            '2026-12-01', 'COP', 'Transferencia', 100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, false,
            0.00, 119000.00, NULL, '{MemberId}', '{MemberId}', '{CreatedAt}', 1);
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, fulfillment_status, source, notes, converted_at,
            converted_by, order_date, created_at, updated_at, version, client_id, advisor_id, currency, payment_method, subtotal,
            tax_percentage, tax_amount, discount_amount, total, bills_to_final_consumer, billing_uses_business_name,
            is_store_pickup, is_retail, customer_vat_surplus, retention_amount, net_total, billing_company_id, billing_bank_name,
            billing_account_number, billing_account_currency, billing_company_country)
        VALUES (
            '{orderId}', '{TenantId}', 'PED-{orderId[^5..]}', NULL, 'Open', 'PaymentPending', 'NotPrepared', 'Manual', NULL,
            '{CreatedAt}', '{MemberId}', '2026-11-20', '{CreatedAt}', '{CreatedAt}', 1, '{MemberId}', '{MemberId}', 'COP',
            'Transferencia', 100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false, false, false, 0.00, 119000.00,
            '{MemberId}', 'Bancolombia', '7542', 'COP', 'CO');
        """;

    private static async Task AssertCheckViolationAsync(string connectionString, string sql, string constraint)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
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
