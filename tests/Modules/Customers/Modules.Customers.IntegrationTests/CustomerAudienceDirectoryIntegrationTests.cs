using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-11 §4.4 contra la base: la audiencia (etiquetas más sueltos) sin duplicados, aislada por tenant,
/// paginada por clave en el orden de PostgreSQL; el conteo en cubos excluyentes; y la marca de opt-out idempotente.</summary>
public sealed class CustomerAudienceDirectoryIntegrationTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    // Clientes incompletos por SQL: crearlos uno a uno por el directorio tardaría minutos y esta prueba trata de la consulta.
    private static Task InsertCustomersAsync(
        string connectionString, Guid tenant, int count, string prefix, string columns = "", string values = "") =>
        ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness, phone_e164{columns})
            SELECT gen_random_uuid(), '{tenant}', '{prefix} ' || n, true, false, false, 1, now(), now(), 'Incomplete', '+5730000' || lpad(n::text, 5, '0'){values}
            FROM generate_series(1, {count}) AS n
            """);

    private static async Task InsertTagAsync(string connectionString, Guid tenant, Guid tag, string name) =>
        await ExecuteAsync(
            connectionString,
            $"INSERT INTO customers.tags (id, tenant_id, name, color, version, created_at, updated_at) VALUES ('{tag}', '{tenant}', '{name}', 'blue', 1, now(), now())");

    private static Task TagAllAsync(string connectionString, Guid tenant, Guid tag, string prefix) =>
        ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO customers.customer_tags (customer_id, tag_id, tenant_id, assigned_by_user_id, assigned_at)
            SELECT id, '{tag}', tenant_id, '{Guid.Empty}', now() FROM customers.customers WHERE tenant_id = '{tenant}' AND name LIKE '{prefix} %'
            """);

    private static async Task<List<CustomerContact>> AudienceAsync(
        QepApiFactory factory, Guid tenant, Guid[] tags, Guid[] customers)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        var list = new List<CustomerContact>();
        await foreach (var contact in directory.ListAudienceAsync(tenant, tags, customers, TestContext.Current.CancellationToken))
        {
            list.Add(contact);
        }

        return list;
    }

    [Fact]
    public async Task ABigAudienceIsReadInPostgresIdOrderWithoutSkippingOrRepeating()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var tag = Guid.NewGuid();
        await InsertCustomersAsync(connection, Tenant, 1_150, "Masivo");
        await InsertCustomersAsync(connection, Tenant, 30, "Fuera");
        await InsertTagAsync(connection, Tenant, tag, "Masivo");
        await TagAllAsync(connection, Tenant, tag, "Masivo");

        var audience = await AudienceAsync(factory, Tenant, [tag], []);

        var expected = await ColumnAsync(connection,
            $"SELECT c.id FROM customers.customers c JOIN customers.customer_tags t ON t.customer_id = c.id WHERE t.tag_id = '{tag}' ORDER BY c.id");
        Assert.Equal(1_150, audience.Count);
        Assert.Equal(expected, audience.Select(contact => contact.CustomerId.ToString()));
    }

    [Fact]
    public async Task ACustomerReachedByTwoTagsAndByIdAppearsOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await InsertCustomersAsync(connection, Tenant, 3, "Uno");
        await InsertTagAsync(connection, Tenant, first, "Primera");
        await InsertTagAsync(connection, Tenant, second, "Segunda");
        await TagAllAsync(connection, Tenant, first, "Uno");
        await TagAllAsync(connection, Tenant, second, "Uno");
        var explicitId = Guid.Parse((await ColumnAsync(connection, "SELECT id FROM customers.customers ORDER BY name LIMIT 1"))[0]);

        var audience = await AudienceAsync(factory, Tenant, [first, second], [explicitId]);

        Assert.Equal(3, audience.Count);
        Assert.Equal(3, audience.Select(contact => contact.CustomerId).Distinct().Count());
    }

    [Fact]
    public async Task OnlyExplicitCustomersAndOnlyOfTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var tag = Guid.NewGuid();
        await InsertCustomersAsync(connection, Tenant, 2, "Mio");
        await InsertCustomersAsync(connection, OtherTenant, 2, "Ajeno");
        await InsertTagAsync(connection, OtherTenant, tag, "Ajena");
        await TagAllAsync(connection, OtherTenant, tag, "Ajeno");
        var mine = Guid.Parse((await ColumnAsync(connection, $"SELECT id FROM customers.customers WHERE tenant_id = '{Tenant}' ORDER BY name LIMIT 1"))[0]);
        var foreign = Guid.Parse((await ColumnAsync(connection, $"SELECT id FROM customers.customers WHERE tenant_id = '{OtherTenant}' LIMIT 1"))[0]);

        // Ni el cliente de otro tenant pedido por id, ni la etiqueta de otro tenant, entran a la audiencia.
        var audience = await AudienceAsync(factory, Tenant, [tag], [mine, foreign]);

        Assert.Equal(mine, Assert.Single(audience).CustomerId);
    }

    [Fact]
    public async Task CountsSplitIntoExclusiveBucketsWithPrecedence()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var tag = Guid.NewGuid();
        await InsertTagAsync(connection, Tenant, tag, "Todos");
        await InsertCustomersAsync(connection, Tenant, 4, "Normal");
        await InsertCustomersAsync(connection, Tenant, 2, "Inactivo", ", marketing_opt_out", ", true");
        await ExecuteAsync(connection, "UPDATE customers.customers SET is_active = false WHERE name LIKE 'Inactivo %'");
        await InsertCustomersAsync(connection, Tenant, 3, "SinWa");
        await ExecuteAsync(connection, "UPDATE customers.customers SET phone_e164 = NULL WHERE name LIKE 'SinWa %'");
        await InsertCustomersAsync(connection, Tenant, 5, "Opt", ", marketing_opt_out", ", true");
        foreach (var prefix in new[] { "Normal", "Inactivo", "SinWa", "Opt" })
        {
            await TagAllAsync(connection, Tenant, tag, prefix);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>()
            .CountAudienceAsync(Tenant, [tag], [], TestContext.Current.CancellationToken);

        // Los inactivos con opt-out cuentan como inactivos, no como opt-out: cada cliente cae en un solo cubo.
        Assert.Equal(new AudienceCount(Total: 14, Inactive: 2, OptedOut: 5, WithoutWhatsApp: 3), count);
    }

    [Fact]
    public async Task MarkingTheOptOutIsIdempotentKeepsTheFirstRecordAndDoesNotBumpTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        await InsertCustomersAsync(connection, Tenant, 1, "Laura");
        await InsertCustomersAsync(connection, OtherTenant, 1, "Ajena");
        var id = Guid.Parse((await ColumnAsync(connection, $"SELECT id FROM customers.customers WHERE tenant_id = '{Tenant}'"))[0]);
        var foreign = Guid.Parse((await ColumnAsync(connection, $"SELECT id FROM customers.customers WHERE tenant_id = '{OtherTenant}'"))[0]);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
            await directory.MarkMarketingOptOutAsync(Tenant, id, TestContext.Current.CancellationToken);
            var firstAt = (await ColumnAsync(connection, $"SELECT marketing_opt_out_at::text FROM customers.customers WHERE id = '{id}'"))[0];
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await directory.MarkMarketingOptOutAsync(Tenant, id, TestContext.Current.CancellationToken);
            await directory.MarkMarketingOptOutAsync(Tenant, foreign, TestContext.Current.CancellationToken);

            Assert.Equal(
                $"true|meta|1|{firstAt}",
                (await ColumnAsync(connection, $"SELECT marketing_opt_out || '|' || marketing_opt_out_source || '|' || version || '|' || marketing_opt_out_at::text FROM customers.customers WHERE id = '{id}'"))[0]);
            // Un cliente de otro tenant no se toca aunque se conozca su id.
            Assert.Equal("False", (await ColumnAsync(connection, $"SELECT marketing_opt_out FROM customers.customers WHERE id = '{foreign}'"))[0]);
        }
    }

    [Fact]
    public async Task FindContactIsScopedToTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        await InsertCustomersAsync(connection, Tenant, 1, "Laura");
        var id = Guid.Parse((await ColumnAsync(connection, "SELECT id FROM customers.customers"))[0]);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        var mine = await directory.FindContactAsync(Tenant, id, TestContext.Current.CancellationToken);
        var foreign = await directory.FindContactAsync(OtherTenant, id, TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerContact(id, "Laura 1", "+573000000001", null, true, false), mine);
        Assert.Null(foreign);
    }
}
