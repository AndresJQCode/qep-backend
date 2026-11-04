using System.Net;
using System.Net.Http.Json;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La libreta de direcciones con dos carriles de ciudad (spec tienda publica 4.2, opcion B):
/// Colombia lleva <c>cityId</c>, el resto del mundo lleva <c>cityName</c>. Para lo colombiano nada
/// cambia, y eso tambien se prueba.
/// </summary>
public sealed class CustomerAddressesApiTests
{
    private static string AddressesUrl(Guid customerId) =>
        $"{CustomersUrl()}/{customerId}/addresses";

    private static async Task<CustomerResponse> ReadAsync(HttpResponseMessage response)
    {
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(customer);
        return customer;
    }

    [Fact]
    public async Task ExistingAddressesReadAsColombian()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var legacyId = Guid.NewGuid();

        // Una fila con la forma anterior a la migracion: sin country ni city_name.
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO customers.customer_addresses (
                    id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
                VALUES (@id, @customer, 'Bodega vieja', 'Calle 1 # 2-3', NULL, @city,
                        false, '2026-09-01T12:00:00Z', '2026-09-01T12:00:00Z')
                """,
                connection);
            insert.Parameters.AddWithValue("id", legacyId);
            insert.Parameters.AddWithValue("customer", customer.Id);
            insert.Parameters.AddWithValue("city", city.CityId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var read = await GetAsync(client, customer.Id);

        var legacy = Assert.Single(read.Addresses, address => address.Id == legacyId);
        Assert.Equal("CO", legacy.Country);
        Assert.Equal(city.CityId, legacy.CityId);
        Assert.Equal(city.CityName, legacy.CityName);
        Assert.Equal(city.DepartmentId, legacy.DepartmentId);
        Assert.Equal(city.DepartmentName, legacy.DepartmentName);
        Assert.All(read.Addresses, address => Assert.Equal("CO", address.Country));
    }

    [Fact]
    public async Task AForeignAddressRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var response = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new
            {
                name = "Sede Madrid",
                address = "Calle Gran Via 28",
                country = "ES",
                cityName = "Madrid",
                isPrincipal = false
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await ReadAsync(response);
        var foreign = Assert.Single(created.Addresses, address => address.Country == "ES");
        Assert.Null(foreign.CityId);
        Assert.Null(foreign.DepartmentId);
        Assert.Null(foreign.DepartmentName);
        Assert.Equal("Madrid", foreign.CityName);

        var read = await GetAsync(client, customer.Id);
        Assert.Contains(read.Addresses, address => address.Country == "ES" && address.CityName == "Madrid");
    }

    [Fact]
    public async Task AColombianAddressRequestWithoutCountryIsUnchanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var created = await AddAddressAsync(client, customer.Id, city.CityId);

        var added = Assert.Single(created.Addresses, address => address.Name == "Bodega Norte");
        Assert.Equal("CO", added.Country);
        Assert.Equal(city.CityId, added.CityId);
        Assert.Equal(city.CityName, added.CityName);
        Assert.Equal(city.DepartmentId, added.DepartmentId);
    }

    [Fact]
    public async Task ALaneMismatchIs422WithItsField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);

        var foreignWithId = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle 1", country = "ES", cityId = city.CityId, cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken);
        var foreignWithoutName = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle 1", country = "ES", isPrincipal = false },
            TestContext.Current.CancellationToken);
        var colombianWithName = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle 1", cityId = city.CityId, cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken);
        var colombianWithoutCity = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle 1", country = "CO", isPrincipal = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignWithId.StatusCode);
        Assert.Contains("CityId", await ValidationFieldsAsync(foreignWithId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignWithoutName.StatusCode);
        Assert.Contains("CityName", await ValidationFieldsAsync(foreignWithoutName));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, colombianWithName.StatusCode);
        Assert.Contains("CityName", await ValidationFieldsAsync(colombianWithName));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, colombianWithoutCity.StatusCode);
        Assert.Contains("CityId", await ValidationFieldsAsync(colombianWithoutCity));
    }

    [Fact]
    public async Task AForeignAddressCanBeEdited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var created = await ReadAsync(await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle Gran Via 28", country = "ES", cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken));
        var foreign = Assert.Single(created.Addresses, address => address.Country == "ES");

        var response = await client.PutAsJsonAsync(
            $"{AddressesUrl(customer.Id)}/{foreign.Id}",
            new { name = "Sede", address = "Passeig de Gracia 1", country = "ES", cityName = "Barcelona", isPrincipal = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = Assert.Single(
            (await ReadAsync(response)).Addresses, address => address.Id == foreign.Id);
        Assert.Equal("Barcelona", edited.CityName);
        Assert.Equal("Passeig de Gracia 1", edited.Address);
        Assert.Null(edited.CityId);
    }

    [Fact]
    public async Task AnAddressCanMoveBetweenLanes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var withBodega = await AddAddressAsync(client, customer.Id, city.CityId);
        var bodega = Assert.Single(withBodega.Addresses, address => address.Name == "Bodega Norte");

        var toForeign = await client.PutAsJsonAsync(
            $"{AddressesUrl(customer.Id)}/{bodega.Id}",
            new { name = "Bodega Norte", address = "Calle 1", country = "ES", cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, toForeign.StatusCode);
        var asForeign = Assert.Single(
            (await ReadAsync(toForeign)).Addresses, address => address.Id == bodega.Id);
        Assert.Equal("ES", asForeign.Country);
        Assert.Null(asForeign.CityId);

        var toColombian = await client.PutAsJsonAsync(
            $"{AddressesUrl(customer.Id)}/{bodega.Id}",
            new { name = "Bodega Norte", address = "Calle 1", cityId = city.CityId, isPrincipal = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, toColombian.StatusCode);
        var asColombian = Assert.Single(
            (await ReadAsync(toColombian)).Addresses, address => address.Id == bodega.Id);
        Assert.Equal("CO", asColombian.Country);
        Assert.Equal(city.CityId, asColombian.CityId);
        Assert.Equal(city.CityName, asColombian.CityName);
    }

    [Fact]
    public async Task EditingWithALaneMismatchIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var created = await ReadAsync(await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle Gran Via 28", country = "ES", cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken));
        var foreign = Assert.Single(created.Addresses, address => address.Country == "ES");

        var response = await client.PutAsJsonAsync(
            $"{AddressesUrl(customer.Id)}/{foreign.Id}",
            new { name = "Sede", address = "Calle Gran Via 28", country = "ES", cityId = city.CityId, cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("CityId", await ValidationFieldsAsync(response));
        var unchanged = Assert.Single(
            (await GetAsync(client, customer.Id)).Addresses, address => address.Id == foreign.Id);
        Assert.Equal("ES", unchanged.Country);
        Assert.Equal("Madrid", unchanged.CityName);
    }

    [Fact]
    public async Task ACustomerPutWithoutCountryKeepsColombianAddresses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);
        await AddAddressAsync(client, customer.Id, cities[1].CityId);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{customer.Id}",
            NewCustomerBody(cities[0].CityId, classification.Id, name: "Verde Esencial Renovada"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync(response);
        Assert.Equal(2, updated.Addresses.Count);
        Assert.All(updated.Addresses, address => Assert.Equal("CO", address.Country));
        Assert.All(updated.Addresses, address => Assert.NotNull(address.CityId));
    }

    [Fact]
    public async Task CityFiltersIgnoreForeignAddresses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var cities = await EnsureCitiesAsync(client, 2);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, cities[0].CityId, classification.Id);
        var response = await client.PostAsJsonAsync(
            AddressesUrl(customer.Id),
            new { name = "Sede", address = "Calle Gran Via 28", country = "ES", cityName = "Madrid", isPrincipal = false },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var byOtherCity = await ListAsync(client, $"?cityIds={cities[1].CityId}");
        var byDomicile = await ListAsync(client, $"?cityIds={cities[0].CityId}");

        Assert.Empty(byOtherCity.Items);
        var item = Assert.Single(byDomicile.Items);
        Assert.Equal(customer.Id, item.Id);
    }
}
