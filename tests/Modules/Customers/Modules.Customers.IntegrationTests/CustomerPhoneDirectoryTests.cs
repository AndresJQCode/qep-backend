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
        Assert.Equal(new CustomerPhoneMatch(first.Id, "Primero"), match.Value);

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
