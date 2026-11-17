using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La migración de datos de impuestos por tipo (spec 2026-11-10 §11.2 y §11.3, owner D10, plan P6): cada tenant que
/// retenía con la bandera fija recibe la tasa «Retefuente 2,5 %», sus clientes con retención la reciben asignada y cada
/// cotización y pedido existente recibe su foto. Ningún total cambia.
///
/// El host arranca y migra todo (Catalog y Customers incluidos, que corren antes que Quotations); después Quotations baja
/// a <c>AddOrderWithholdings</c>, se siembra el estado anterior por SQL y vuelve a subir, como <see cref="FreightMigrationTests"/>.
/// La conversión de las tasas existentes a IVA/CO es de la Tarea 2 y la cubre
/// <c>TaxRateTypeMigrationTests.ExistingRatesBecomeIvaColombia</c>; acá sólo se verifica que esta migración no la toca
/// (<see cref="NameTakenUsesMigratedSuffix"/>).
/// </summary>
public sealed class RetentionToWithholdingsMigrationTests
{
    private const string LastMigrationBeforeTheData = "_AddOrderWithholdings";
    private const string RateName = "Retefuente 2,5 %";
    private const string MigratedRateName = "Retefuente 2,5 % (migrada)";

    private const string TenantA = "01900000-0000-7000-8000-0000000e0a01";
    private const string TenantB = "01900000-0000-7000-8000-0000000e0b01";
    private const string TenantC = "01900000-0000-7000-8000-0000000e0c01";
    private const string MemberId = "01900000-0000-7000-8000-0000000e0f01";

    [Fact]
    public async Task CreatesOneRetefuentePerTenantThatUsedIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);

        await MigrateFromLegacyAsync(
            connectionString,
            Customer(TenantA, Id(0xa101), withRetention: true)
            + Customer(TenantA, Id(0xa102), withRetention: true)
            + Quotation(TenantB, Id(0xb201), customerWithRetention: false, partyWithRetention: true, billingParty: true)
            + Customer(TenantC, Id(0xc101), withRetention: false)
            + Quotation(TenantC, Id(0xc201), customerWithRetention: false, partyWithRetention: null, retentionAmount: 0m));

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            Assert.Equal(
                $"{RateName}|Retefuente|CO|2.500||True|1",
                await ScalarAsync<string>(
                    connectionString,
                    "SELECT name || '|' || type || '|' || country || '|' || percentage::text || '|' "
                    + "|| coalesce(minimum_base::text, '') || '|' || initcap(is_active::text) || '|' || version::text "
                    + $"FROM catalog.tax_rates WHERE tenant_id = '{tenant}'"));
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM catalog.tax_rates WHERE tenant_id = '{TenantC}'"));
    }

    [Fact]
    public async Task NameTakenUsesMigratedSuffix()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var vatId = Id(0xa301);

        await MigrateFromLegacyAsync(
            connectionString,
            Rate(TenantA, vatId, RateName, "IVA", 19m)
            + Customer(TenantA, Id(0xa101), withRetention: true));

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            $"SELECT count(*) FROM catalog.tax_rates WHERE tenant_id = '{TenantA}' AND name = '{MigratedRateName}' "
            + "AND type = 'Retefuente' AND country = 'CO' AND percentage = 2.5"));
        // La tasa que ya estaba sigue como la dejó la Tarea 2: IVA de Colombia, sin tocar.
        Assert.Equal(
            "IVA|CO|19.000|1",
            await ScalarAsync<string>(
                connectionString,
                $"SELECT type || '|' || country || '|' || percentage::text || '|' || version::text "
                + $"FROM catalog.tax_rates WHERE id = '{vatId}'"));
    }

    [Fact]
    public async Task AnEquivalentRetefuenteIsReusedInsteadOfDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var existing = Id(0xa301);
        var customer = Id(0xa101);

        await MigrateFromLegacyAsync(
            connectionString,
            Rate(TenantA, existing, "Retención compras", "Retefuente", 2.5m)
            + Customer(TenantA, customer, withRetention: true));

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM catalog.tax_rates WHERE tenant_id = '{TenantA}'"));
        Assert.Equal(Guid.Parse(existing), await ScalarAsync<Guid>(
            connectionString, $"SELECT tax_rate_id FROM customers.customer_tax_rates WHERE customer_id = '{customer}'"));
    }

    [Fact]
    public async Task AssignsTheRateToEveryCustomerWithRetention()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);

        await MigrateFromLegacyAsync(
            connectionString,
            Customer(TenantA, Id(0xa101), withRetention: true)
            + Customer(TenantA, Id(0xa102), withRetention: true)
            + Customer(TenantA, Id(0xa103), withRetention: false)
            + Customer(TenantB, Id(0xb101), withRetention: true));

        var rateA = await MigratedRateAsync(connectionString, TenantA);
        var rateB = await MigratedRateAsync(connectionString, TenantB);
        Assert.Equal(
            $"{TenantA}|{Id(0xa101)}|{rateA};{TenantA}|{Id(0xa102)}|{rateA};{TenantB}|{Id(0xb101)}|{rateB}",
            await ScalarAsync<string>(
                connectionString,
                "SELECT string_agg(tenant_id || '|' || customer_id || '|' || tax_rate_id, ';' ORDER BY customer_id) "
                + "FROM customers.customer_tax_rates"));
    }

    // Un cliente fusionado ya no se edita ni se cotiza: no recibe la tasa, y así no la deja «en uso» para siempre.
    [Fact]
    public async Task MergedCustomersDoNotGetTheRate()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var survivor = Id(0xa101);
        var merged = Id(0xa102);

        await MigrateFromLegacyAsync(
            connectionString,
            Customer(TenantA, survivor, withRetention: true)
            + Customer(TenantA, merged, withRetention: true, mergedInto: survivor));

        Assert.Equal(
            new[] { survivor },
            await ListAsync(
                connectionString,
                "SELECT customer_id::text FROM customers.customer_tax_rates ORDER BY customer_id"));
    }

    // Los dos nombres de la migración ya los tienen otras tasas: la tercera lleva un sufijo propio y el arranque no falla.
    [Fact]
    public async Task BothNamesTakenUsesAUniqueSuffix()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);

        await MigrateFromLegacyAsync(
            connectionString,
            Rate(TenantA, Id(0xa301), RateName, "IVA", 19m)
            + Rate(TenantA, Id(0xa302), MigratedRateName, "IVA", 5m)
            + Customer(TenantA, Id(0xa101), withRetention: true));

        var name = await ScalarAsync<string>(
            connectionString,
            $"SELECT name FROM catalog.tax_rates WHERE tenant_id = '{TenantA}' AND type = 'Retefuente'");
        Assert.Matches(@"^Retefuente 2,5 % \(migrada [0-9a-f]{8}\)$", name);
        Assert.Equal(
            new[] { Id(0xa101) },
            await ListAsync(connectionString, "SELECT customer_id::text FROM customers.customer_tax_rates"));
    }

    [Fact]
    public async Task QuotationGetsCustomerAndBillingRows()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var withCustomerData = Id(0xa201);
        var withOwnBilling = Id(0xa202);
        var withoutRetention = Id(0xa203);

        await MigrateFromLegacyAsync(
            connectionString,
            Quotation(TenantA, withCustomerData, customerWithRetention: true, partyWithRetention: null)
            + Quotation(TenantA, withOwnBilling, customerWithRetention: true, partyWithRetention: true, billingParty: true)
            + Quotation(TenantA, withoutRetention, customerWithRetention: false, partyWithRetention: false, billingParty: true,
                retentionAmount: 0m));

        var rate = await MigratedRateAsync(connectionString, TenantA);
        Assert.Equal(
            new[]
            {
                $"{withCustomerData}|Customer|0|{rate}|{RateName}|Retefuente|CO|2.500|||",
                $"{withOwnBilling}|Billing|0|{rate}|{RateName}|Retefuente|CO|2.500|||",
                $"{withOwnBilling}|Customer|0|{rate}|{RateName}|Retefuente|CO|2.500|||",
            },
            await RowsAsync(connectionString, "quotation_withholdings"));
    }

    [Fact]
    public async Task OrderGetsOneDocumentRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var ownPartyRetains = Id(0xa401);
        var customerRetains = Id(0xa402);
        var ownPartyDoesNotRetain = Id(0xa403);

        await MigrateFromLegacyAsync(
            connectionString,
            Order(TenantA, ownPartyRetains, customerWithRetention: false, partyWithRetention: true, billingParty: true)
            + Order(TenantA, customerRetains, customerWithRetention: true, partyWithRetention: null)
            // Con parte propia manda lo que se contestó ahí, aunque el cliente retenga.
            + Order(TenantA, ownPartyDoesNotRetain, customerWithRetention: true, partyWithRetention: false, billingParty: true,
                retentionAmount: 0m));

        var rate = await MigratedRateAsync(connectionString, TenantA);
        Assert.Equal(
            new[]
            {
                $"{ownPartyRetains}|Document|0|{rate}|{RateName}|Retefuente|CO|2.500|||",
                $"{customerRetains}|Document|0|{rate}|{RateName}|Retefuente|CO|2.500|||",
            },
            await RowsAsync(connectionString, "order_withholdings"));
    }

    [Fact]
    public async Task AppliedRowsCarryTodaysAmount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        var applied = Id(0xa201);
        var usd = Id(0xa202);
        var finalConsumer = Id(0xa203);
        var appliedOrder = Id(0xa401);
        var usdOrder = Id(0xa402);

        await MigrateFromLegacyAsync(
            connectionString,
            Quotation(TenantA, applied, customerWithRetention: true, partyWithRetention: true, billingParty: true)
            + Quotation(TenantA, usd, customerWithRetention: true, partyWithRetention: null, currency: "USD", retentionAmount: 0m)
            + Quotation(TenantA, finalConsumer, customerWithRetention: true, partyWithRetention: null,
                billsToFinalConsumer: true, retentionAmount: 0m)
            + Order(TenantA, appliedOrder, customerWithRetention: true, partyWithRetention: null)
            + Order(TenantA, usdOrder, customerWithRetention: true, partyWithRetention: null, currency: "USD",
                retentionAmount: 0m));

        Assert.Equal(
            new[]
            {
                // Con parte propia la fuente vigente es la de facturación: la del cliente queda sin aplicar.
                $"{applied}|Billing|True|100000.00|2500.00",
                $"{applied}|Customer|False|0.00|0.00",
                $"{usd}|Customer|False|0.00|0.00",
                $"{finalConsumer}|Customer|False|0.00|0.00",
            },
            await AmountsAsync(connectionString, "quotation_withholdings"));
        Assert.Equal(
            new[]
            {
                $"{appliedOrder}|Document|True|100000.00|2500.00",
                $"{usdOrder}|Document|False|0.00|0.00",
            },
            await AmountsAsync(connectionString, "order_withholdings"));
    }

    [Fact]
    public async Task NoTotalChanges()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = StartHost(connectionString);
        const string Totals = "string_agg(id || '|' || subtotal || '|' || total || '|' || retention_amount || '|' || net_total, ';' ORDER BY id)";
        string? quotationsBefore = null;
        string? ordersBefore = null;

        await MigrateFromLegacyAsync(
            connectionString,
            Quotation(TenantA, Id(0xa201), customerWithRetention: true, partyWithRetention: true, billingParty: true)
            + Quotation(TenantA, Id(0xa202), customerWithRetention: true, partyWithRetention: null, currency: "USD", retentionAmount: 0m)
            // Un total guardado que el cálculo de hoy no daría: la migración tampoco lo corrige.
            + Quotation(TenantA, Id(0xa203), customerWithRetention: true, partyWithRetention: null, retentionAmount: 1234.56m)
            + Order(TenantA, Id(0xa401), customerWithRetention: true, partyWithRetention: null)
            + Order(TenantA, Id(0xa402), customerWithRetention: false, partyWithRetention: true, billingParty: true,
                retentionAmount: 777.77m),
            beforeMigratingUp: async () =>
            {
                quotationsBefore = await ScalarAsync<string>(connectionString, $"SELECT {Totals} FROM quotations.quotations");
                ordersBefore = await ScalarAsync<string>(connectionString, $"SELECT {Totals} FROM quotations.orders");
            });

        // Por tenant: el host puede sembrar el tenant de desarrollo (con su IVA) si Seed:Enabled llega por la configuración local.
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM catalog.tax_rates WHERE tenant_id = '{TenantA}'"));
        Assert.Equal(quotationsBefore, await ScalarAsync<string>(connectionString, $"SELECT {Totals} FROM quotations.quotations"));
        Assert.Equal(ordersBefore, await ScalarAsync<string>(connectionString, $"SELECT {Totals} FROM quotations.orders"));
    }

    // Review Focus 1: una cotización abierta sin empresa que hoy retiene por el cliente. Tras la migración tiene su foto,
    // pero la lectura no recalcula: el GET devuelve lo guardado, aunque sin empresa la próxima edición la lleve a 0.
    [Fact]
    public async Task MigratedDraftKeepsItsStoredTotalsOnRead()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        // El estado de antes del despliegue: la bandera fija en el cliente y en la cotización, y la retención de 2,5 %
        // sobre la base de 100.000 guardada en los totales.
        await MigrateFromLegacyAsync(
            connectionString,
            $"""
            UPDATE customers.customers SET with_retention = true WHERE id = '{customerId}';
            DELETE FROM quotations.quotation_withholdings WHERE document_id = '{draft.Id}';
            UPDATE quotations.quotations
            SET customer_with_retention = true, retention_amount = 2500.00, net_total = total - 2500.00
            WHERE id = '{draft.Id}';
            """);

        var read = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{draft.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        Assert.Equal(100_000m, read.Subtotal);
        Assert.Equal(2_500m, read.RetentionAmount);
        Assert.Equal(read.Total - 2_500m, read.NetTotal);
        Assert.Equal(
            new[] { await MigratedRateAsync(connectionString, tenantId.ToString()) },
            read.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        // La lectura es pura (spec §7.6): la fila guardada no cambió.
        Assert.Equal(
            new[] { $"{draft.Id}|Customer|True|100000.00|2500.00" },
            await AmountsAsync(connectionString, "quotation_withholdings"));
    }

    /// <summary>Arranca el host, que migra todos los módulos a la última migración.</summary>
    private static QepApiFactory StartHost(string connectionString)
    {
        var factory = new QepApiFactory(connectionString);
        _ = factory.Services;
        return factory;
    }

    /// <summary>Baja Quotations a la última migración anterior a los datos, siembra y vuelve a subir.</summary>
    private static async Task MigrateFromLegacyAsync(
        string connectionString, string seedSql, Func<Task>? beforeMigratingUp = null)
    {
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, LastMigrationBeforeTheData), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, $"""
            SET session_replication_role = replica;
            {seedSql}
            RESET session_replication_role;
            """);
        if (beforeMigratingUp is not null)
        {
            await beforeMigratingUp();
        }

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
    }

    private static string Id(int suffix) => $"01900000-0000-7000-8000-0000000{suffix:x5}";

    private static string Customer(string tenantId, string id, bool withRetention, string? mergedInto = null) => $"""
        INSERT INTO customers.customers (
            id, tenant_id, name, completeness, country, is_active, marketing_opt_out, vat_surplus, with_retention, version, created_at,
            updated_at, merged_into_id, merged_at)
        VALUES ('{id}', '{tenantId}', 'Cliente {id}', 'Incomplete', 'CO', {Bool(mergedInto is null)}, false, false,
            {Bool(withRetention)}, 1, now(), now(), {(mergedInto is null ? "NULL" : $"'{mergedInto}'")},
            {(mergedInto is null ? "NULL" : "now()")});

        """;

    private static string Rate(string tenantId, string id, string name, string type, decimal percentage) => $"""
        INSERT INTO catalog.tax_rates
            (id, tenant_id, name, percentage, type, country, minimum_base, is_active, version, created_at, updated_at)
        VALUES ('{id}', '{tenantId}', '{name}', {Number(percentage)}, '{type}', 'CO', NULL, true, 1, now(), now());

        """;

    // Base 100.000, IVA 19.000, total 119.000; la retención guardada es la del 2,5 % salvo que la prueba diga otra.
    private static string Quotation(
        string tenantId,
        string id,
        bool customerWithRetention,
        bool? partyWithRetention,
        bool billingParty = false,
        string currency = "COP",
        bool billsToFinalConsumer = false,
        decimal retentionAmount = 2500m) => $"""
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, created_at, valid_until, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total, billing_uses_business_name, is_store_pickup, is_retail,
            bills_to_final_consumer, customer_with_retention, party_with_retention, customer_vat_surplus, retention_amount,
            net_total, notes, created_by, updated_by, updated_at, version)
        VALUES (
            '{id}', '{tenantId}', 'COT-{id[^5..]}', '{Id(0xfff01)}', '{MemberId}', 'Draft', '2026-11-01T15:00:00Z',
            '2026-12-01', '{currency}', 'Transferencia', 100000.00, 19.00, 19000.00, 0.00, 119000.00, false, false, false,
            {Bool(billsToFinalConsumer)}, {Bool(customerWithRetention)}, {Bool(partyWithRetention)}, false,
            {Number(retentionAmount)}, {Number(119000m - retentionAmount)}, NULL, '{MemberId}', '{MemberId}',
            '2026-11-01T15:00:00Z', 1);
        {(billingParty ? $"INSERT INTO quotations.quotation_parties (id, quotation_id, role, name) VALUES (gen_random_uuid(), '{id}', 'Billing', 'Sede');" : string.Empty)}

        """;

    private static string Order(
        string tenantId,
        string id,
        bool customerWithRetention,
        bool? partyWithRetention,
        bool billingParty = false,
        string currency = "COP",
        decimal retentionAmount = 2500m) => $"""
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version, client_id, advisor_id,
            currency, payment_method, subtotal, tax_percentage, tax_amount, discount_amount, total, bills_to_final_consumer,
            customer_with_retention, party_with_retention, retention_amount, net_total, billing_company_id, billing_bank_name,
            billing_account_number, billing_account_currency, billing_company_country)
        VALUES (
            '{id}', '{tenantId}', 'PED-{id[^5..]}', NULL, 'Pending', 'PaymentPending', NULL, '2026-11-02T15:00:00Z',
            '{MemberId}', NULL, NULL, NULL, '2026-11-02T15:00:00Z', '2026-11-02T15:00:00Z', 1, '{Id(0xfff01)}', '{MemberId}',
            '{currency}', 'Transferencia', 100000.00, 19.00, 19000.00, 0.00, 119000.00, false,
            {Bool(customerWithRetention)}, {Bool(partyWithRetention)}, {Number(retentionAmount)},
            {Number(119000m - retentionAmount)}, '{Id(0xfff02)}', 'Bancolombia', '7542', '{currency}', 'CO');
        {(billingParty ? $"INSERT INTO quotations.order_parties (id, order_id, role, name) VALUES (gen_random_uuid(), '{id}', 'Billing', 'Sede');" : string.Empty)}

        """;

    private static string Bool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "NULL",
    };

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static Task<Guid> MigratedRateAsync(string connectionString, string tenantId) =>
        ScalarAsync<Guid>(
            connectionString,
            $"SELECT id FROM catalog.tax_rates WHERE tenant_id = '{tenantId}' AND type = 'Retefuente'");

    private static Task<string[]> RowsAsync(string connectionString, string table) =>
        ListAsync(
            connectionString,
            "SELECT document_id || '|' || source || '|' || position || '|' || tax_rate_id || '|' || name || '|' || type "
            + "|| '|' || country || '|' || percentage::text || '|' || coalesce(minimum_base::text, '') || '|' "
            + "|| coalesce(minimum_base_unit, '') || '|' || coalesce(tax_unit_value::text, '') "
            + $"FROM quotations.{table} ORDER BY document_id, source");

    private static Task<string[]> AmountsAsync(string connectionString, string table) =>
        ListAsync(
            connectionString,
            "SELECT document_id || '|' || source || '|' || initcap(applied::text) || '|' || base_amount::text || '|' "
            + $"|| amount::text FROM quotations.{table} ORDER BY document_id, source");

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(QuotationsDbContext context, string suffix) =>
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
