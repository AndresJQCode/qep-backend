using System.Net.Http.Json;
using Bootstrapper.Seeding;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La carga sintética para medir la exportación (spec 2026-09-13, Sección 2). Vive en este proyecto
/// porque es el que sabe leer los listados y correr los exports sobre lo sembrado.
/// </summary>
public sealed class ExportLoadSeedTests
{
    private const string OwnerEmail = "carga@qcode.co";

    // TenancySeeder siembra el tenant de la carga en America/Bogota: su hoy es el que decide qué vence
    // (spec 2026-09-17, punto 8c).
    private static readonly TimeZoneInfo LoadTenantTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    // Spec 2026-09-17, punto 8c: la carga marca Expired con el hoy del tenant, el mismo del barrido de
    // vencimiento. Con el reloj en el 31 de diciembre a las 23:00 de Bogotá, lo que vence el 31 sigue
    // enviado. Entre las 200 hay al menos una que vence ese día (hallazgo 10 del plan: la n = 53).
    [Fact]
    public async Task TheSeedExpiresWithTheTenantsLocalToday()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var connectionString = database.GetConnectionString();
        var lastDay = new DateOnly(2026, 12, 31);

        await factory.Services.SeedExportLoadAsync(OwnerEmail, 200, TestContext.Current.CancellationToken);

        Assert.NotEqual(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until = @lastDay",
            ("lastDay", lastDay)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until = @lastDay AND status <> 'Sent'",
            ("lastDay", lastDay)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until < @lastDay AND status <> 'Expired'",
            ("lastDay", lastDay)));
    }

    // Prendida sin email, la carga le daría admin a nadie sobre un tenant al que nadie puede entrar.
    // Mismo criterio que Seed:Enabled (SeedStartupTests).
    [Fact]
    public async Task TheLoadSwitchWithoutOwnerEmailFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "5");
            builder.UseSetting("Seed:OwnerEmail", string.Empty);
        });

        // ThrowsAny: ValidateOnStart lanza durante el arranque, y WebApplicationFactory puede
        // entregarla envuelta.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Seed:OwnerEmail is required when Seed:ExportLoad:Quotations is greater than 0",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANegativeLoadFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "-1");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Seed:ExportLoad:Quotations cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSeedFillsItsOwnTenantWithConsistentRowsAndNoSideEffects()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        // El host corre las migraciones al arrancar, y la factoría no arranca hasta que alguien le pide
        // Services: sin esto, platform.outbox_messages todavía no existe. El "hoy" sale del mismo IClock
        // con que decide el seeder, en el huso del tenant de la carga (spec 2026-09-17, punto 8c), y se
        // lee antes de sembrar, no del now() de la base, que en una corrida que cruce la medianoche ya
        // sería otro día.
        await using var clockScope = factory.Services.CreateAsyncScope();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            clockScope.ServiceProvider.GetRequiredService<IClock>().UtcNow, LoadTenantTimeZone).DateTime);
        var outboxBefore = await ScalarAsync<long>(connectionString, "SELECT count(*) FROM platform.outbox_messages");

        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);

        Assert.True(result.Seeded);
        // Un cliente cada 25 cotizaciones, tres líneas por cotización, el 30 % convertido.
        Assert.Equal((8, 200, 600, 60), (result.Customers, result.Quotations, result.Items, result.Orders));
        Assert.Equal("carga-export", await ScalarAsync<string>(
            connectionString, "SELECT slug FROM tenancy.tenants WHERE id = @tenant"));
        Assert.Equal("seed-export-load", await ScalarAsync<string>(
            connectionString,
            "SELECT origin FROM tenancy.memberships WHERE tenant_id = @tenant AND state = 'Active' AND 'admin' = ANY(roles)"));
        // Enviadas vigentes o vencidas: nada que el barrido de vencimiento tenga que tocar hoy.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotations
            WHERE tenant_id = @tenant
              AND NOT ((status = 'Sent' AND valid_until >= @today)
                    OR (status = 'Expired' AND valid_until < @today))
            """, ("today", today)));
        // Cada total es la suma de sus líneas, ninguna está "editada después de enviar", y todas caen
        // en los últimos 12 meses.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotations AS quotation
            WHERE quotation.tenant_id = @tenant
              AND (quotation.total <> (
                       SELECT sum(item.subtotal + item.tax_amount)
                       FROM quotations.quotation_items AS item
                       WHERE item.quotation_id = quotation.id)
                   OR quotation.net_total <> quotation.total
                   OR quotation.updated_at <> quotation.sent_at
                   OR quotation.created_at < now() - interval '365 days')
            """));
        // El IVA sale de adentro del precio: la línea vale cantidad × precio, y el impuesto es la parte de
        // esa línea que le toca a la tarifa. Sumarlo encima del precio rompe la primera; calcularlo sobre la
        // base y no sobre la línea, la segunda. Sin líneas gravadas las dos pasarían sin probar nada.
        Assert.NotEqual(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotation_items AS item
            JOIN quotations.quotations AS quotation ON quotation.id = item.quotation_id
            WHERE quotation.tenant_id = @tenant AND item.tax_percentage > 0
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotation_items AS item
            JOIN quotations.quotations AS quotation ON quotation.id = item.quotation_id
            WHERE quotation.tenant_id = @tenant
              AND item.subtotal + item.tax_amount <> round(item.quantity * item.unit_price, 2)
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotation_items AS item
            JOIN quotations.quotations AS quotation ON quotation.id = item.quotation_id
            WHERE quotation.tenant_id = @tenant
              AND item.tax_amount <> round(
                      (item.subtotal + item.tax_amount) * item.tax_percentage / (100 + item.tax_percentage), 2)
            """));
        // Ni outbox, ni auditoría, ni historial: el SQL masivo no pasa por los handlers.
        Assert.Equal(outboxBefore, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM platform.outbox_messages"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenant"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotation_history AS history
            JOIN quotations.quotations AS quotation ON quotation.id = history.quotation_id
            WHERE quotation.tenant_id = @tenant
            """));

        // Idempotente: una segunda corrida ve cotizaciones en el tenant y no siembra.
        var again = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        Assert.False(again.Seeded);
        Assert.Equal(200L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task TheListsAndTheExportsReadTheLoad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        var ownerUserId = await ScalarAsync<Guid>(
            database.GetConnectionString(), "SELECT id FROM identity.users WHERE email = @email", ("email", OwnerEmail));
        using var client = CreateClient(
            factory, ownerUserId.ToString(), ExportLoadSeeder.TenantId.ToString(), ManagerPermissions);

        var quotations = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(ExportLoadSeeder.TenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(quotations);
        Assert.Equal(result.Quotations, quotations.Total);
        Assert.All(quotations.Items, item =>
        {
            Assert.NotNull(item.ClientName);
            // El owner sembrado nace con CreateActive, sin nombre: la fila cae a su correo.
            Assert.Equal(OwnerEmail, item.AdvisorName);
            // Líneas, vigencia y cuenta de cobro: lo que hace que un pedido haya podido salir de ahí.
            Assert.True(item.IsComplete);
        });

        var orders = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{ExportLoadSeeder.TenantId}/orders", TestContext.Current.CancellationToken);
        Assert.NotNull(orders);
        Assert.Equal(result.Orders, orders.Total);
        Assert.All(orders.Items, item =>
        {
            Assert.StartsWith("PED-", item.OrderNumber, StringComparison.Ordinal);
            Assert.NotNull(item.ClientName);
            // Mismo respaldo que la fila de cotizaciones: sin nombre, el correo del owner.
            Assert.Equal(OwnerEmail, item.AdvisorName);
            Assert.Equal("PaymentPending", item.PaymentStatus);
        });

        // Los procesadores de verdad sobre un año entero, igual que un pedido desde la pantalla, con el
        // hoy del tenant (spec 2026-09-17).
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LoadTenantTimeZone).DateTime);
        var quotationsJob = await EnqueueExportJobAsync(
            factory, ExportLoadSeeder.TenantId, ownerUserId, ExportJobKind.Quotations,
            ExportJobFilters.Serialize(new QuotationsExportFilters(null, null, null, today.AddYears(-1), today, null, null)));
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));
        Assert.Equal(result.Quotations, (await FindExportJobAsync(factory, quotationsJob)).RowCount);

        var ordersJob = await EnqueueExportJobAsync(
            factory, ExportLoadSeeder.TenantId, ownerUserId, ExportJobKind.Orders,
            ExportJobFilters.Serialize(new OrdersExportFilters(null, null, null, null, today.AddYears(-1), today, null, null)));
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));
        // El Excel de pedidos es una fila por línea de producto (ajuste 2026-09-20), y cada
        // cotización sembrada tiene las mismas ExportLoadSeeder.ItemsPerQuotation líneas.
        Assert.Equal(
            result.Orders * ExportLoadSeeder.ItemsPerQuotation,
            (await FindExportJobAsync(factory, ordersJob)).RowCount);
    }

    // Los tres contadores quedan en el siguiente al último sembrado: el primer alta real del tenant no
    // choca contra un índice único.
    [Fact]
    public async Task TheCountersContinueAfterTheLoad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        var ownerUserId = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM identity.users WHERE email = @email", ("email", OwnerEmail));
        using var client = CreateClient(
            factory, ownerUserId.ToString(), ExportLoadSeeder.TenantId.ToString(), ManagerPermissions);
        // El año del consecutivo es el del tenant desde la spec 2026-09-17 (punto 2a).
        var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LoadTenantTimeZone).Year;
        var quotationsThisYear = await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND quotation_number LIKE @prefix",
            ("prefix", $"QUO-{year}-%"));
        var seededClientId = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM customers.customers WHERE tenant_id = @tenant ORDER BY cuc LIMIT 1");

        var quotation = await CreateQuotationAsync(client, ExportLoadSeeder.TenantId, seededClientId);
        Assert.Equal($"QUO-{year}-{quotationsThisYear + 1:D4}", quotation.QuotationNumber);

        var customerId = await CreateActiveCustomerAsync(client, ExportLoadSeeder.TenantId);
        var cuc = await ScalarAsync<string>(
            connectionString, "SELECT cuc FROM customers.customers WHERE id = @id", ("id", customerId));
        Assert.EndsWith($"{result.Customers + 1:D6}", cuc, StringComparison.Ordinal);

        // Pedidos: un contador por año, cada uno en el siguiente al último sembrado.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM (
                SELECT extract(year FROM converted_at AT TIME ZONE 'UTC')::int AS year, count(*) + 1 AS expected
                FROM quotations.orders
                WHERE tenant_id = @tenant
                GROUP BY 1) AS seeded
            FULL JOIN (
                SELECT year, next_value FROM quotations.order_number_counters WHERE tenant_id = @tenant) AS counter
              ON counter.year = seeded.year
            WHERE counter.next_value IS DISTINCT FROM seeded.expected
            """));
    }

    // Después del arranque, nunca dentro: el startupProbe le da al pod 60 s como máximo.
    [Fact]
    public async Task WithTheSwitchOnTheHostSeedsOnItsOwnAfterStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "20");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

        var worker = factory.Services.GetServices<IHostedService>().OfType<ExportLoadSeedWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(20L, await ScalarAsync<long>(
            database.GetConnectionString(), "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task WithTheSwitchAtZeroTheHostSeedsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var worker = factory.Services.GetServices<IHostedService>().OfType<ExportLoadSeedWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(
            database.GetConnectionString(), "SELECT count(*) FROM tenancy.tenants WHERE id = @tenant"));
    }

    // La limpieza borra la carga y nada más: otro tenant con datos propios queda intacto, y el usuario
    // dueño —la cuenta real de quien midió— también.
    [Fact]
    public async Task TheCleanupScriptRemovesOnlyTheLoadTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = otherClient;
        var otherCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var otherQuotation = await CreateQuotationAsync(otherClient, otherTenantId, otherCustomerId);
        await factory.Services.SeedExportLoadAsync(OwnerEmail, 50, TestContext.Current.CancellationToken);
        await EnqueueExportJobAsync(factory, ExportLoadSeeder.TenantId, Guid.CreateVersion7());

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                await File.ReadAllTextAsync(CleanupScriptPath(), TestContext.Current.CancellationToken),
                connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        string[] tenantTables =
        [
            "quotations.orders", "quotations.quotations", "quotations.quotation_number_counters",
            "quotations.order_number_counters", "quotations.export_jobs", "customers.customers",
            "customers.client_classifications", "customers.cuc_counters", "catalog.products",
            "catalog.tax_rates", "tenancy.memberships",
        ];
        foreach (var table in tenantTables)
        {
            Assert.Equal(0L, await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM {table} WHERE tenant_id = @tenant"));
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM tenancy.tenants WHERE id = @tenant"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE id = @id", ("id", otherQuotation.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM identity.users WHERE email = @email", ("email", OwnerEmail)));
    }

    // Se busca hacia arriba desde bin/, mismo criterio que ConfigurationExampleTests.
    private static string CleanupScriptPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ops", "export-load-cleanup.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"ops/export-load-cleanup.sql not found above {AppContext.BaseDirectory}.");
    }

    // @tenant siempre apunta al tenant de la carga; los demás parámetros van por nombre.
    private static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@tenant", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("tenant", ExportLoadSeeder.TenantId);
        }

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static List<string> MessagesOf(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return messages;
    }
}
