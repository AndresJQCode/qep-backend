using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Phones;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-09 §6.5: un cliente por número (D-M7), búsqueda por nombre con tope, y el
/// backfill que llena phone_e164 de las filas viejas sin tocar su versión.</summary>
public sealed class CustomerPhoneDirectoryTests
{
    [Fact]
    public async Task MatchReturnsTheOldestCustomerPerPhoneAndOnlyWithinTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateOtherTenantManager(factory);
        var myCity = await EnsureCityAsync(mine);
        var myClassification = await CreateClassificationAsync(mine);
        var theirCity = await EnsureCityAsync(theirs);
        var theirClassification = await CreateClassificationAsync(theirs, "Mediano", "CLI", OtherTenantId);

        var first = await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Primero", "900.111.111-1", "300 123 4567");
        var second = await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Segundo", "900.222.222-2", "300 123 4567");
        await CreateWithPhoneAsync(
            theirs, theirCity.CityId, theirClassification.Id, "Ajeno", "900.333.333-3", "300 123 4567", OtherTenantId);

        var matches = await MatchAsync(factory, ["+573001234567", "+570000000000"]);

        var match = Assert.Single(matches);
        Assert.Equal("+573001234567", match.Key);
        Assert.Equal(new CustomerPhoneMatch(first.Id, "Primero", true), match.Value);

        // D-M7: manda created_at, no el orden de inserción ni el id. Si el segundo pasa a ser el más
        // viejo, gana él.
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE customers.customers SET created_at = created_at - interval '1 day' WHERE id = @id",
            ("id", second.Id));
        matches = await MatchAsync(factory, ["+573001234567"]);
        Assert.Equal(second.Id, Assert.Single(matches).Value.Id);

        // Y con created_at empatado, el id menor (en el orden de PostgreSQL, que es el de la consulta).
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE customers.customers SET created_at = TIMESTAMPTZ '2026-01-01 00:00:00+00' WHERE id = ANY(@ids)",
            ("ids", new[] { first.Id, second.Id }));
        var smallerId = await ScalarAsync<Guid>(
            database.GetConnectionString(),
            "SELECT id FROM customers.customers WHERE id = ANY(@ids) ORDER BY id LIMIT 1",
            ("ids", new[] { first.Id, second.Id }));
        matches = await MatchAsync(factory, ["+573001234567"]);
        Assert.Equal(smallerId, Assert.Single(matches).Value.Id);
    }

    // Spec 2026-10-24 §3.3: un fusionado nunca es «el más viejo» del número, aunque lo sea por created_at.
    [Fact]
    public async Task MatchNeverPicksAMergedAsTheOldest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        var city = await EnsureCityAsync(mine);
        var classification = await CreateClassificationAsync(mine);
        var oldest = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "El Fusionado", "900.111.111-1", "300 123 4567");
        var survivor = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Quien Queda", "900.222.222-2", "300 123 4567");
        await MarkMergedAsync(database.GetConnectionString(), oldest.Id, survivor.Id);

        var matches = await MatchAsync(factory, ["+573001234567"]);

        Assert.Equal(new CustomerPhoneMatch(survivor.Id, "Quien Queda", true), Assert.Single(matches).Value);
    }

    // §3.3: el número sólo lo tiene el fusionado; sale el que quedó, con su nombre y su completitud.
    [Fact]
    public async Task AMatchOnlyAMergedHasResolvesToTheSurvivor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        var city = await EnsureCityAsync(mine);
        var classification = await CreateClassificationAsync(mine);
        var merged = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "El Fusionado", "900.111.111-1", "300 123 4567");
        var survivor = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Quien Queda", "900.222.222-2", "300 999 9999");
        await MarkMergedAsync(database.GetConnectionString(), merged.Id, survivor.Id);

        var matches = await MatchAsync(factory, ["+573001234567", "+573009999999"]);

        Assert.Equal(new CustomerPhoneMatch(survivor.Id, "Quien Queda", true), matches["+573001234567"]);
        Assert.Equal(new CustomerPhoneMatch(survivor.Id, "Quien Queda", true), matches["+573009999999"]);
    }

    // Spec 2026-11-10: se filtra antes de elegir; un inactivo viejo no esconde al activo.
    [Fact]
    public async Task MatchActiveFiltersBeforePickingTheOldest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        var city = await EnsureCityAsync(mine);
        var classification = await CreateClassificationAsync(mine);
        var oldInactive = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Viejo Inactivo", "900.111.111-1", "300 123 4567");
        var newActive = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Nuevo Activo", "900.222.222-2", "300 123 4567");
        var onlyInactive = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Solo Inactivo", "900.333.333-3", "300 555 0000");
        var onlyMerged = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Solo Fusionado", "900.444.444-4", "300 777 0000");
        var survivor = await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Quien Queda", "900.555.555-5", "300 999 9999");
        var connectionString = database.GetConnectionString();
        await MarkMergedAsync(connectionString, onlyMerged.Id, survivor.Id);
        // El fusionado vuelve a is_active = true para que lo descarte el filtro de fusionados y no el de activos.
        await ExecuteAsync(
            connectionString,
            "UPDATE customers.customers SET is_active = false WHERE id = ANY(@ids)",
            ("ids", new[] { oldInactive.Id, onlyInactive.Id }));
        await ExecuteAsync(
            connectionString,
            "UPDATE customers.customers SET is_active = true WHERE id = @id",
            ("id", onlyMerged.Id));
        await ExecuteAsync(
            connectionString,
            "UPDATE customers.customers SET created_at = created_at - interval '1 day' WHERE id = @id",
            ("id", oldInactive.Id));

        var matches = await MatchActiveAsync(factory, ["+573001234567", "+573005550000", "+573007770000"]);

        var match = Assert.Single(matches);
        Assert.Equal("+573001234567", match.Key);
        Assert.Equal(new CustomerPhoneMatch(newActive.Id, "Nuevo Activo", true), match.Value);
    }

    [Fact]
    public async Task MatchActiveStaysWithinTheTenantAndAnswersNothingForNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var theirs = CreateOtherTenantManager(factory);
        var theirCity = await EnsureCityAsync(theirs);
        var theirClassification = await CreateClassificationAsync(theirs, "Mediano", "CLI", OtherTenantId);
        await CreateWithPhoneAsync(
            theirs, theirCity.CityId, theirClassification.Id, "Ajeno", "900.333.333-3", "300 123 4567", OtherTenantId);

        Assert.Empty(await MatchActiveAsync(factory, ["+573001234567"]));
        Assert.Empty(await MatchActiveAsync(factory, []));
    }

    [Fact]
    public async Task ListByPhoneReturnsEveryCustomerOfTheNumberOldestFirstWithinTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateOtherTenantManager(factory);
        var myCity = await EnsureCityAsync(mine);
        var myClassification = await CreateClassificationAsync(mine);
        var theirCity = await EnsureCityAsync(theirs);
        var theirClassification = await CreateClassificationAsync(theirs, "Mediano", "CLI", OtherTenantId);

        var first = await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Primero", "900.111.111-1", "300 123 4567");
        var second = await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Segundo", "900.222.222-2", "300 123 4567");
        var third = await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Tercero", "900.333.333-3", "300 123 4567");
        await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Otro numero", "900.444.444-4", "300 999 9999");
        await CreateWithPhoneAsync(
            theirs, theirCity.CityId, theirClassification.Id, "Ajeno", "900.555.555-5", "300 123 4567", OtherTenantId);
        await ExecuteAsync(database.GetConnectionString(), "UPDATE customers.customers SET is_active = false WHERE id = @id", ("id", second.Id));
        // El orden manda por created_at, no por el orden de inserción: el tercero pasa a ser el más viejo.
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE customers.customers SET created_at = created_at - interval '1 day' WHERE id = @id",
            ("id", third.Id));

        var listed = await ListByPhoneAsync(factory, "+573001234567", 10);

        Assert.Equal(
            [new CustomerPhoneMatch(third.Id, "Tercero", true), new CustomerPhoneMatch(first.Id, "Primero", true), new CustomerPhoneMatch(second.Id, "Segundo", true)],
            listed);
    }

    [Fact]
    public async Task ListByPhoneHonoursTheTake()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        var city = await EnsureCityAsync(mine);
        var classification = await CreateClassificationAsync(mine);
        await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Uno", "900.111.111-1", "300 123 4567");
        await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Dos", "900.222.222-2", "300 123 4567");
        await CreateWithPhoneAsync(mine, city.CityId, classification.Id, "Tres", "900.333.333-3", "300 123 4567");

        var listed = await ListByPhoneAsync(factory, "+573001234567", 2);

        Assert.Equal(["Uno", "Dos"], listed.Select(match => match.Name));
    }

    [Fact]
    public async Task FindPhonesByNameHonoursTheCapAndTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateManager(factory);
        using var theirs = CreateOtherTenantManager(factory);
        var myCity = await EnsureCityAsync(mine);
        var myClassification = await CreateClassificationAsync(mine);
        var theirCity = await EnsureCityAsync(theirs);
        var theirClassification = await CreateClassificationAsync(theirs, "Mediano", "CLI", OtherTenantId);

        await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Drogueria Norte", "900.111.111-1", "301 111 1111");
        await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Drogueria Sur", "900.222.222-2", "302 222 2222");
        await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Drogueria Centro", "900.333.333-3", "304 444 4444");
        await CreateWithPhoneAsync(mine, myCity.CityId, myClassification.Id, "Ferreteria Norte", "900.444.444-4", "310 000 0000");
        await CreateWithPhoneAsync(
            theirs, theirCity.CityId, theirClassification.Id, "Drogueria Ajena", "900.555.555-5", "305 555 5555", OtherTenantId);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerPhoneDirectory>();
        var tenantId = Guid.Parse(TenantId);
        string[] mineDrugstores = ["+573011111111", "+573022222222", "+573044444444"];

        var capped = await directory.FindPhonesByNameAsync(tenantId, "drogueria", 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, capped.Count);
        Assert.All(capped, phone => Assert.Contains(phone, mineDrugstores));

        var all = await directory.FindPhonesByNameAsync(tenantId, "drogueria", 10, TestContext.Current.CancellationToken);
        Assert.Equal(mineDrugstores.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheBackfillFillsOldRowsWithoutBumpingTheirVersion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateWithPhoneAsync(client, city.CityId, classification.Id, "Verde", "900.111.111-1", "310 935 2187");
        var unparseable = await CreateWithPhoneAsync(client, city.CityId, classification.Id, "Raro", "900.222.222-2", "310 935 2188");

        var worker = factory.Services.GetServices<IHostedService>().OfType<CustomerPhoneBackfillWorker>().Single();
        // La corrida del arranque tiene que haber terminado: si no, podría llenar la fila por su
        // cuenta y la prueba no distinguiría quién lo hizo.
        await worker.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("+573109352187", await PhoneE164Async(connectionString, customer.Id));
        // La fila vieja: phone_e164 sin calcular. Y una con un teléfono que no parsea, que el
        // backfill tiene que saltar sin quedarse en un ciclo.
        await ExecuteAsync(connectionString, "UPDATE customers.customers SET phone_e164 = NULL WHERE id = @id", ("id", customer.Id));
        await ExecuteAsync(
            connectionString, "UPDATE customers.customers SET phone = '12', phone_e164 = NULL WHERE id = @id", ("id", unparseable.Id));
        var versionBefore = await ScalarAsync<long>(connectionString, "SELECT version FROM customers.customers WHERE id = @id", ("id", customer.Id));
        var updatedAtBefore = await ScalarAsync<DateTime>(
            connectionString, "SELECT updated_at FROM customers.customers WHERE id = @id", ("id", customer.Id));

        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("+573109352187", await PhoneE164Async(connectionString, customer.Id));
        Assert.Null(await PhoneE164Async(connectionString, unparseable.Id));
        Assert.Equal(versionBefore, await ScalarAsync<long>(connectionString, "SELECT version FROM customers.customers WHERE id = @id", ("id", customer.Id)));
        Assert.Equal(
            updatedAtBefore,
            await ScalarAsync<DateTime>(connectionString, "SELECT updated_at FROM customers.customers WHERE id = @id", ("id", customer.Id)));

        // Idempotente: otra corrida no cambia nada.
        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("+573109352187", await PhoneE164Async(connectionString, customer.Id));
        Assert.Equal(versionBefore, await ScalarAsync<long>(connectionString, "SELECT version FROM customers.customers WHERE id = @id", ("id", customer.Id)));
    }

    private static HttpClient CreateOtherTenantManager(QepApiFactory factory) =>
        CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

    private static async Task<CustomerResponse> CreateWithPhoneAsync(
        HttpClient client,
        Guid cityId,
        Guid classificationId,
        string name,
        string identificationNumber,
        string phone,
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            CustomersUrl(tenantId),
            NewCustomerBody(cityId, classificationId, name: name, identificationNumber: identificationNumber, phone: phone),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    private static async Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
        QepApiFactory factory, IReadOnlyCollection<string> phones)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerPhoneDirectory>();
        return await directory.MatchAsync(Guid.Parse(TenantId), phones, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchActiveAsync(
        QepApiFactory factory, IReadOnlyCollection<string> phones)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerPhoneDirectory>();
        return await directory.MatchActiveAsync(Guid.Parse(TenantId), phones, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(QepApiFactory factory, string e164, int take)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerPhoneDirectory>();
        return await directory.ListByPhoneAsync(Guid.Parse(TenantId), e164, take, TestContext.Current.CancellationToken);
    }

    private static Task<string?> PhoneE164Async(string connectionString, Guid customerId) =>
        ScalarAsync<string?>(connectionString, "SELECT phone_e164 FROM customers.customers WHERE id = @id", ("id", customerId));

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? default! : (T)result;
    }
}
