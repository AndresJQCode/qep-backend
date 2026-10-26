using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec de Shopify 2026-10-15 §6.2.1 contra la base: la búsqueda por correo sin mayúsculas y aislada por tenant
/// (sobre <c>IX_customers_tenant_email</c>), y las dos altas de un comprador desde una integración, con su auditoría de
/// actor <c>Integration</c> en el outbox.</summary>
public sealed class CustomerProvisioningIntegrationTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<T> InScopeAsync<T, TService>(QepApiFactory factory, Func<TService, Task<T>> action)
        where TService : notnull
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task FindIdByEmailIgnoresCaseAndOtherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // compras@verde.co

        var found = await InScopeAsync<CustomerId?, ICustomerRepository>(factory, repository =>
            repository.FindIdByEmailAsync(Tenant, " COMPRAS@Verde.CO ", TestContext.Current.CancellationToken));
        var otherTenant = await InScopeAsync<CustomerId?, ICustomerRepository>(factory, repository =>
            repository.FindIdByEmailAsync(Guid.CreateVersion7(), "compras@verde.co", TestContext.Current.CancellationToken));
        var unknown = await InScopeAsync<CustomerId?, ICustomerRepository>(factory, repository =>
            repository.FindIdByEmailAsync(Tenant, "otra@verde.co", TestContext.Current.CancellationToken));

        Assert.Equal(new CustomerId(existing.Id), found);
        Assert.Null(otherTenant);
        Assert.Null(unknown);
        Assert.Equal("CREATE INDEX \"IX_customers_tenant_email\" ON customers.customers USING btree (tenant_id, lower((email)::text))",
            await ScalarAsync(database.GetConnectionString(),
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'customers' AND indexname = 'IX_customers_tenant_email'"));
    }

    [Fact]
    public async Task CreateCompletePersistsWithCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var data = new CompleteCustomerData(
            "Laura Pérez", "CC", "1020304050", "310 935 2187", "Laura@Verde.co", "Calle 10 # 45-12", "CO", city.CityId, null,
            classification.Id);

        var created = await InScopeAsync<ProvisionedCustomer, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CreateCompleteAsync(Tenant, data, TestContext.Current.CancellationToken));

        Assert.Equal(new ProvisionedCustomer(created.Id, true, true, true, 1), created);
        Assert.Equal($"Complete|CLI{city.DepartmentDivipolaCode}000001|Cc|1020304050|laura@verde.co|+573109352187|false|false|1",
            await ScalarAsync(database.GetConnectionString(),
                "SELECT completeness || '|' || cuc || '|' || identification_type || '|' || identification_number || '|' || email || '|' || "
                + "phone_e164 || '|' || with_retention || '|' || vat_surplus || '|' || "
                + $"(SELECT count(*) FROM customers.customer_addresses WHERE customer_id = c.id) FROM customers.customers c WHERE id = '{created.Id}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.created' "
            + $"AND payload->>'resourceId' = '{created.Id}' AND payload->>'actorType' = 'Integration' AND payload->>'actorId' = '{Guid.Empty}'"));
    }

    [Fact]
    public async Task CreateIncompletePersistsIncomplete()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);

        var created = await InScopeAsync<ProvisionedCustomer, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CreateIncompleteAsync(Tenant, null, "+573001234567", " Comprador@Tienda.co ", "CO", TestContext.Current.CancellationToken));

        Assert.Equal(new ProvisionedCustomer(created.Id, false, true, true, 1), created);
        Assert.Equal("Incomplete|Cliente de Shopify|comprador@tienda.co|CO|+573001234567|true|true|true",
            await ScalarAsync(database.GetConnectionString(),
                "SELECT completeness || '|' || name || '|' || email || '|' || country || '|' || phone_e164 || '|' || "
                + "(cuc IS NULL) || '|' || (identification_number IS NULL) || '|' || (classification_id IS NULL) "
                + $"FROM customers.customers WHERE id = '{created.Id}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.created' "
            + $"AND payload->>'resourceId' = '{created.Id}' AND payload->>'actorType' = 'Integration'"));
    }

    // Perder la carrera del documento no deja la fila perdedora (ni su outbox) Added en el scope. Sin el
    // Clear del unit of work, el siguiente SaveChanges del mismo scope la reintenta y vuelve a dar identification_taken.
    [Fact]
    public async Task CreateCompleteLostRaceLeavesTheScopeUsable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, city.CityId, classification.Id); // NIT 900.123.456-1
        var sameDocument = new CompleteCustomerData(
            "Verde otra vez", "NIT", "900.123.456-1", "310 000 0000", "otra@verde.co", "Calle 1", "CO", city.CityId, null,
            classification.Id);
        var otherBuyer = new CompleteCustomerData(
            "Laura Pérez", "CC", "1020304050", "310 935 2187", "laura@verde.co", "Calle 10 # 45-12", "CO", city.CityId, null,
            classification.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<ICustomerProvisioning>();
        var lost = await provisioning.CreateCompleteAsync(Tenant, sameDocument, TestContext.Current.CancellationToken);
        var next = await provisioning.CreateCompleteAsync(Tenant, otherBuyer, TestContext.Current.CancellationToken);
        var incomplete = await provisioning.CreateIncompleteAsync(Tenant, "Comprador", null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(new ProvisionedCustomer(existing.Id, true, true, false, 1), lost);
        Assert.True(next.Created);
        Assert.True(incomplete.Created);
        Assert.Equal("3", await ScalarAsync(database.GetConnectionString(),
            $"SELECT count(*) FROM customers.customers WHERE tenant_id = '{Tenant}'"));
        // La auditoría de la perdedora tampoco sobrevive: sólo las dos altas que entraron.
        Assert.Equal("2", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.created' "
            + "AND payload->>'actorType' = 'Integration'"));
    }

    // Spec de ciudades 2026-10-26 §4.4 contra la base: el incompleto de la venta externa se completa con el CUC del
    // departamento de la ciudad, su libreta nace con la dirección principal y la auditoría sale una vez como Integration.
    [Fact]
    public async Task CompleteExternalSalePersistsCucAddressAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await InScopeAsync<ProvisionedCustomer, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CreateIncompleteAsync(Tenant, "Laura Pérez", "310 935 2187", "laura@verde.co", "CO", TestContext.Current.CancellationToken));
        var data = new CompleteCustomerData(
            "Laura Pérez", "CC", "1020304050", "310 935 2187", "Laura@Verde.co", "Calle 10 # 45-12", "CO", city.CityId, null,
            classification.Id);

        var completed = await InScopeAsync<ExternalCustomerCityOutcome, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CompleteExternalSaleAsync(Tenant, incomplete.Id, incomplete.Version, data, TestContext.Current.CancellationToken));
        var again = await InScopeAsync<ExternalCustomerCityOutcome, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CompleteExternalSaleAsync(Tenant, incomplete.Id, incomplete.Version, data, TestContext.Current.CancellationToken));

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.Applied, 2, null), completed);
        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.AlreadyApplied, 2, null), again);
        Assert.Equal(
            $"Complete|CLI{city.DepartmentDivipolaCode}000001|Cc|1020304050|{city.CityId}|2|1|{city.CityId}|true",
            await ScalarAsync(database.GetConnectionString(),
                "SELECT completeness || '|' || cuc || '|' || identification_type || '|' || identification_number || '|' || city_id || '|' || version || '|' || "
                + $"(SELECT count(*) FROM customers.customer_addresses WHERE customer_id = c.id) || '|' || "
                + $"(SELECT city_id FROM customers.customer_addresses WHERE customer_id = c.id) || '|' || "
                + $"(SELECT is_principal FROM customers.customer_addresses WHERE customer_id = c.id) FROM customers.customers c WHERE id = '{incomplete.Id}'"));
        Assert.Equal("1", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.completed' "
            + $"AND payload->>'resourceId' = '{incomplete.Id}' AND payload->>'actorType' = 'Integration'"));
    }

    // D-C4b contra la base: la ciudad del cliente y la de su dirección principal se mudan juntas, con la versión
    // optimista subida, y la corrección no deja auditoría de Customers (la escribe Shopify, P12).
    [Fact]
    public async Task CorrectCityPersistsTheCustomerAndItsPrincipalAddress()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var existing = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);

        var corrected = await InScopeAsync<ExternalCustomerCityOutcome, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CorrectCityAsync(Tenant, existing.Id, 1, cities[0].CityId, cities[1].CityId, TestContext.Current.CancellationToken));

        Assert.Equal(new ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind.Applied, 2, null), corrected);
        Assert.Equal($"{cities[1].CityId}|2|1|{cities[1].CityId}",
            await ScalarAsync(database.GetConnectionString(),
                "SELECT city_id || '|' || version || '|' || "
                + "(SELECT count(*) FROM customers.customer_addresses WHERE customer_id = c.id) || '|' || "
                + $"(SELECT city_id FROM customers.customer_addresses WHERE customer_id = c.id AND is_principal) FROM customers.customers c WHERE id = '{existing.Id}'"));
        Assert.Equal("0", await ScalarAsync(database.GetConnectionString(),
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'resourceId' = "
            + $"'{existing.Id}' AND payload->>'action' <> 'customers.customer.created'"));
    }

    // El payload ya tenía source, fijo en «customers»; la caja lo cambia a «pos» y todo lo demás sigue igual.
    [Fact]
    public async Task TheAuditPayloadCarriesTheGivenSourceAndDefaultsToCustomers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        var actor = Guid.CreateVersion7();

        await InScopeAsync<ProvisionedCustomer, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CreateFromRegisterAsync(
                Tenant, actor, new RegisterCustomerData("Laura Gómez", "CC", "1020304050", null), "pos", TestContext.Current.CancellationToken));
        await InScopeAsync<ProvisionedCustomer, ICustomerProvisioning>(factory, provisioning =>
            provisioning.CreateFromRegisterAsync(
                Tenant, actor, new RegisterCustomerData("Pedro Ruiz", "CC", "1020304051", null), null, TestContext.Current.CancellationToken));

        Assert.Equal("pos", await ScalarAsync(database.GetConnectionString(),
            "SELECT payload->>'source' FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.created' "
            + $"AND payload->>'actorId' = '{actor}' AND payload->>'actorType' = 'Human' "
            + "AND payload->>'resourceId' = (SELECT id::text FROM customers.customers WHERE identification_number = '1020304050')"));
        Assert.Equal("customers", await ScalarAsync(database.GetConnectionString(),
            "SELECT payload->>'source' FROM platform.outbox_messages WHERE payload->>'action' = 'customers.customer.created' "
            + $"AND payload->>'actorId' = '{actor}' AND payload->>'actorType' = 'Human' "
            + "AND payload->>'resourceId' = (SELECT id::text FROM customers.customers WHERE identification_number = '1020304051')"));
    }
}
