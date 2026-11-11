using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec impuestos por tipo §7.4 / §9.2: el cliente recibe y devuelve sus tasas de retención. Las tasas se crean por SQL en
/// <c>catalog.tax_rates</c> (Customers nunca referencia Catalog) y las respuestas se leen como JSON, porque el contrato que
/// se prueba es el de la red y no el de un record.
/// </summary>
public sealed class CustomerWithholdingTaxRatesApiTests
{
    private static async Task<Guid> InsertRateAsync(
        string connectionString,
        string tenantId,
        string name,
        string type,
        decimal percentage,
        bool isActive = true,
        string country = "CO")
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog.tax_rates
                (id, tenant_id, name, percentage, type, country, minimum_base, is_active, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, @percentage, @type, @country, NULL, @isActive, 1, now(), now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("percentage", percentage);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("isActive", isActive);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    // Un diccionario y no un record: cada prueba necesita omitir o mandar campos sueltos (ids ausentes vs. vacíos).
    private static Dictionary<string, object?> Body(Guid cityId, string identificationNumber = "900.123.456-1") => new()
    {
        ["name"] = "Verde Esencial S.A.S.",
        ["identificationType"] = "NIT",
        ["identificationNumber"] = identificationNumber,
        ["phone"] = "310 935 2187",
        ["email"] = "compras@verde.co",
        ["address"] = "Calle 10 # 45-12",
        ["country"] = "CO",
        ["cityId"] = cityId,
        ["vatSurplus"] = false
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> CreateAsync(
        HttpClient client, Dictionary<string, object?> body, HttpStatusCode expected = HttpStatusCode.Created) =>
        await ReadAsync(
            await client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken), expected);

    private static async Task<JsonElement> UpdateAsync(
        HttpClient client, Guid customerId, Dictionary<string, object?> body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadAsync(
            await client.PutAsJsonAsync($"{CustomersUrl()}/{customerId}", body, TestContext.Current.CancellationToken),
            expected);

    private static Guid[] RateIds(JsonElement customer) =>
        [.. customer.GetProperty("withholdingTaxRates").EnumerateArray().Select(rate => rate.GetProperty("id").GetGuid())];

    [Fact]
    public async Task CreateWithRetefuenteAndReteIcaReturnsBoth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var retefuente = await InsertRateAsync(database.GetConnectionString(), TenantId, "Retefuente servicios", "Retefuente", 4m);
        var reteIca = await InsertRateAsync(database.GetConnectionString(), TenantId, "ReteICA Bogota", "ReteICA", 0.966m);
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { retefuente, reteIca };

        var customer = await CreateAsync(client, body);

        var rates = customer.GetProperty("withholdingTaxRates").EnumerateArray().ToArray();
        Assert.Equal(2, rates.Length);
        var first = rates.Single(rate => rate.GetProperty("id").GetGuid() == retefuente);
        Assert.Equal("Retefuente servicios", first.GetProperty("name").GetString());
        Assert.Equal("Retefuente", first.GetProperty("type").GetString());
        Assert.Equal("CO", first.GetProperty("country").GetString());
        Assert.Equal(4m, first.GetProperty("percentage").GetDecimal());
        Assert.True(first.GetProperty("isActive").GetBoolean());
        var second = rates.Single(rate => rate.GetProperty("id").GetGuid() == reteIca);
        Assert.Equal(0.966m, second.GetProperty("percentage").GetDecimal());
        Assert.True(customer.GetProperty("withRetention").GetBoolean());

        var read = await ReadAsync(
            await client.GetAsync($"{CustomersUrl()}/{customer.GetProperty("id").GetGuid()}", TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        Assert.Equivalent(new[] { retefuente, reteIca }, RateIds(read));
    }

    [Fact]
    public async Task TwoRetefuenteRatesAreRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var first = await InsertRateAsync(database.GetConnectionString(), TenantId, "Retefuente 2.5", "Retefuente", 2.5m);
        var second = await InsertRateAsync(database.GetConnectionString(), TenantId, "Retefuente 4", "Retefuente", 4m);
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { first, second };

        var response = await client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("customers.customer.withholding_type_repeated", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("vat")]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("otherTenant")]
    public async Task InvalidRateIsRejected(string kind)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        var rateId = kind switch
        {
            "vat" => await InsertRateAsync(connectionString, TenantId, "IVA 19", "IVA", 19m),
            "inactive" => await InsertRateAsync(connectionString, TenantId, "Retefuente vieja", "Retefuente", 4m, isActive: false),
            "otherTenant" => await InsertRateAsync(connectionString, OtherTenantId, "Retefuente ajena", "Retefuente", 4m),
            _ => Guid.CreateVersion7()
        };
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { rateId };

        var response = await client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("customers.customer.withholding_tax_rate_invalid", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task UpdateWithoutIdsKeepsRates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var retefuente = await InsertRateAsync(database.GetConnectionString(), TenantId, "Retefuente servicios", "Retefuente", 4m);
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { retefuente };
        var created = await CreateAsync(client, body);

        // El frontend viejo no manda el campo: el PUT no puede borrar lo que no conoce.
        var edit = Body(city.CityId);
        edit["name"] = "Verde Esencial Editada";
        var updated = await UpdateAsync(client, created.GetProperty("id").GetGuid(), edit);

        Assert.Equal("Verde Esencial Editada", updated.GetProperty("name").GetString());
        Assert.Equal([retefuente], RateIds(updated));
        Assert.True(updated.GetProperty("withRetention").GetBoolean());
    }

    [Fact]
    public async Task UpdateWithEmptyListClearsRates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var retefuente = await InsertRateAsync(database.GetConnectionString(), TenantId, "Retefuente servicios", "Retefuente", 4m);
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { retefuente };
        var created = await CreateAsync(client, body);

        var edit = Body(city.CityId);
        edit["withholdingTaxRateIds"] = Array.Empty<Guid>();
        var updated = await UpdateAsync(client, created.GetProperty("id").GetGuid(), edit);

        Assert.Empty(RateIds(updated));
        Assert.False(updated.GetProperty("withRetention").GetBoolean());
    }

    [Fact]
    public async Task LegacyWithRetentionInBodyIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var body = Body(city.CityId);
        body["withRetention"] = true;

        var customer = await CreateAsync(client, body);

        Assert.Empty(RateIds(customer));
        Assert.False(customer.GetProperty("withRetention").GetBoolean());
    }

    [Fact]
    public async Task OnlyReteIcaDerivesWithRetentionFalse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var reteIca = await InsertRateAsync(database.GetConnectionString(), TenantId, "ReteICA Bogota", "ReteICA", 0.966m);
        var body = Body(city.CityId);
        body["withholdingTaxRateIds"] = new[] { reteIca };

        var customer = await CreateAsync(client, body);

        Assert.Equal([reteIca], RateIds(customer));
        Assert.False(customer.GetProperty("withRetention").GetBoolean());
    }
}
