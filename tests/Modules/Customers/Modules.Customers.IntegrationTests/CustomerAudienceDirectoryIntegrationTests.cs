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
        string connectionString, Guid tenant, int count, string prefix, string columns = "", string values = "", int phoneOffset = 0) =>
        ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness, phone_e164{columns})
            SELECT gen_random_uuid(), '{tenant}', '{prefix} ' || n, true, false, false, 1, now(), now(), 'Incomplete', '+5730000' || lpad((n + {phoneOffset})::text, 5, '0'){values}
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

    // Un cliente incompleto con teléfono (o sin él) y bandera de opt-out elegidos, para las pruebas del opt-out por número.
    private static async Task<Guid> InsertWithPhoneAsync(string connectionString, Guid tenant, string name, string? phone, bool optOut)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness, phone_e164, marketing_opt_out)
            VALUES ('{id}', '{tenant}', '{name}', true, false, false, 1, now(), now(), 'Incomplete', {(phone is null ? "NULL" : $"'{phone}'")}, {(optOut ? "true" : "false")})
            """);
        return id;
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
        // Cada grupo con su rango de teléfonos: el opt-out es del número, y compartirlos mezclaría los cubos.
        await InsertCustomersAsync(connection, Tenant, 2, "Inactivo", ", marketing_opt_out", ", true", phoneOffset: 100);
        await ExecuteAsync(connection, "UPDATE customers.customers SET is_active = false WHERE name LIKE 'Inactivo %'");
        await InsertCustomersAsync(connection, Tenant, 3, "SinWa");
        await ExecuteAsync(connection, "UPDATE customers.customers SET phone_e164 = NULL WHERE name LIKE 'SinWa %'");
        await InsertCustomersAsync(connection, Tenant, 5, "Opt", ", marketing_opt_out", ", true", phoneOffset: 200);
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

        Assert.NotNull(mine);
        Assert.Equal(new CustomerContact(id, "Laura 1", "+573000000001", null, true, false), mine with { CreatedAt = null });
        Assert.NotNull(mine.CreatedAt);
        Assert.Null(foreign);
    }

    [Fact]
    public async Task TheAudienceAndTheContactCarryTheCreationDate()
    {
        // Spec 2026-10-24 §4.4 (D-M7): sin conversación, de dos clientes de una campaña con el mismo WhatsApp gana el más viejo, y la
        // fecha sale de la misma proyección que el opt-out por número.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var id = await InsertWithPhoneAsync(connection, Tenant, "Ana Vieja", "+573001112222", optOut: false);
        await ExecuteAsync(connection, $"UPDATE customers.customers SET created_at = '2024-01-02T03:04:05Z' WHERE id = '{id}'");
        var expected = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var audience = await AudienceAsync(factory, Tenant, [], [id]);
        await using var scope = factory.Services.CreateAsyncScope();
        var contact = await scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>()
            .FindContactAsync(Tenant, id, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(audience).CreatedAt);
        Assert.Equal(expected, contact!.CreatedAt);
    }

    [Fact]
    public async Task AnOptOutOnANumberCoversEveryCustomerWithThatNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var a = await InsertWithPhoneAsync(connection, Tenant, "A", "+573001112233", true);
        var b = await InsertWithPhoneAsync(connection, Tenant, "B", "+573001112233", false);
        var c = await InsertWithPhoneAsync(connection, Tenant, "C", "+573009998877", false);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        var contactB = await directory.FindContactAsync(Tenant, b, TestContext.Current.CancellationToken);
        var audience = await AudienceAsync(factory, Tenant, [], [a, b, c]);
        var count = await directory.CountAudienceAsync(Tenant, [], [a, b, c], TestContext.Current.CancellationToken);

        Assert.True(contactB!.MarketingOptOut);
        Assert.Equal(
            [(a, true), (b, true), (c, false)],
            audience.OrderBy(contact => contact.Name, StringComparer.Ordinal).Select(contact => (contact.CustomerId, contact.MarketingOptOut)));
        Assert.Equal(new AudienceCount(Total: 3, Inactive: 0, OptedOut: 2, WithoutWhatsApp: 0), count);
    }

    [Fact]
    public async Task AnOptOutOfAnotherTenantWithTheSameNumberDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        await InsertWithPhoneAsync(connection, OtherTenant, "Ajeno", "+573001112233", true);
        var mine = await InsertWithPhoneAsync(connection, Tenant, "Mio", "+573001112233", false);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();

        Assert.False((await directory.FindContactAsync(Tenant, mine, TestContext.Current.CancellationToken))!.MarketingOptOut);
        Assert.Equal(0, (await directory.CountAudienceAsync(Tenant, [], [mine], TestContext.Current.CancellationToken)).OptedOut);
        Assert.False(Assert.Single(await AudienceAsync(factory, Tenant, [], [mine])).MarketingOptOut);
    }

    [Fact]
    public async Task ACustomerWithoutPhoneOnlyLooksAtItsOwnFlag()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        // Dos sin teléfono: que no tengan número no los vuelve «el mismo número» (NULL no es igual a NULL).
        await InsertWithPhoneAsync(connection, Tenant, "Marcado", null, true);
        var clean = await InsertWithPhoneAsync(connection, Tenant, "Limpio", null, false);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();

        Assert.False((await directory.FindContactAsync(Tenant, clean, TestContext.Current.CancellationToken))!.MarketingOptOut);
        Assert.False(Assert.Single(await AudienceAsync(factory, Tenant, [], [clean])).MarketingOptOut);
    }

    // Spec 2026-10-24 desvío F5: el 131050 de Meta (o el botón) sobre un fusionado es de la persona, así que marca al que
    // quedó, con el origen y la fecha; el fusionado no se escribe.
    [Fact]
    public async Task TheMetaOptOutOfAMergedLandsOnTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var survivor = await InsertWithPhoneAsync(connection, Tenant, "Quien Queda", "+573001112233", false);
        var merged = await InsertWithPhoneAsync(connection, Tenant, "El Fusionado", "+573004445566", false);
        var bystander = await InsertWithPhoneAsync(connection, Tenant, "Otro", "+573007778899", false);
        await MarkMergedAsync(connection, merged, survivor);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>()
                .MarkMarketingOptOutAsync(Tenant, merged, TestContext.Current.CancellationToken);
        }

        Assert.Equal("False", (await ColumnAsync(connection, $"SELECT marketing_opt_out FROM customers.customers WHERE id = '{merged}'"))[0]);
        Assert.Equal(
            "true|meta|1",
            (await ColumnAsync(connection, $"SELECT marketing_opt_out || '|' || marketing_opt_out_source || '|' || version FROM customers.customers WHERE id = '{survivor}'"))[0]);
        Assert.Equal("False", (await ColumnAsync(connection, $"SELECT marketing_opt_out FROM customers.customers WHERE id = '{bystander}'"))[0]);
    }

    // FC-03 sólo reescribe las audiencias Draft/Scheduled. Una campaña que pasó a Sending antes de la fusión y todavía no
    // congeló conserva el id del fusionado: el congelado lo redirige al que quedó (una sola vez, aunque también esté).
    [Fact]
    public async Task AnAudienceThatStillListsAMergedReachesTheSurvivorOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var survivor = await InsertWithPhoneAsync(connection, Tenant, "Quien Queda", "+573001112233", false);
        var merged = await InsertWithPhoneAsync(connection, Tenant, "El Fusionado", "+573004445566", false);
        await MarkMergedAsync(connection, merged, survivor);

        var onlyMerged = await AudienceAsync(factory, Tenant, [], [merged]);
        var both = await AudienceAsync(factory, Tenant, [], [merged, survivor]);
        var foreign = await AudienceAsync(factory, OtherTenant, [], [merged]);

        Assert.Equal(survivor, Assert.Single(onlyMerged).CustomerId);
        Assert.Equal(survivor, Assert.Single(both).CustomerId);
        Assert.Empty(foreign);
        await using var scope = factory.Services.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>()
            .CountAudienceAsync(Tenant, [], [merged], TestContext.Current.CancellationToken);
        Assert.Equal(1, count.Total);
    }

    [Fact]
    public async Task MarkingTheOptOutStillMarksOnlyThatCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var connection = database.GetConnectionString();
        var a = await InsertWithPhoneAsync(connection, Tenant, "A", "+573001112233", false);
        var b = await InsertWithPhoneAsync(connection, Tenant, "B", "+573001112233", false);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>()
                .MarkMarketingOptOutAsync(Tenant, a, TestContext.Current.CancellationToken);
        }

        Assert.Equal("True", (await ColumnAsync(connection, $"SELECT marketing_opt_out FROM customers.customers WHERE id = '{a}'"))[0]);
        Assert.Equal("False", (await ColumnAsync(connection, $"SELECT marketing_opt_out FROM customers.customers WHERE id = '{b}'"))[0]);
    }
}
