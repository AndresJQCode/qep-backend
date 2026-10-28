using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>CD-01: el 422 <c>customers.customer.identification_taken</c> de <c>POST</c>/<c>PUT /customers</c> lleva al dueño
/// del documento en <c>existing</c> (activo o no, completo o no), sin perder <c>code</c> ni <c>traceId</c>.</summary>
public sealed class CustomerDuplicateIdentificationApiTests
{
    private const string TakenCode = "customers.customer.identification_taken";
    private const string OwnerName = "GYSELYS VIVAS PACHECO";
    private const string OwnerNit = "92-1396892";

    private static async Task<HttpResponseMessage> PostSecondAsync(HttpClient client, Guid cityId, Guid classificationId) =>
        await client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(cityId, classificationId, name: "Otro Cliente", identificationNumber: OwnerNit),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task CreateWithAnotherCustomersDocumentReturnsTheOwnerAsExisting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var owner = await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);

        var response = await PostSecondAsync(client, city.CityId, classification.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(TakenCode, body.GetProperty("code").GetString());
        var existing = body.GetProperty("existing");
        Assert.Equal(owner.Id, existing.GetProperty("id").GetGuid());
        Assert.Equal(OwnerName, existing.GetProperty("name").GetString());
        Assert.Equal("NIT", existing.GetProperty("identificationType").GetString());
        Assert.Equal(OwnerNit, existing.GetProperty("identificationNumber").GetString());
        Assert.False(string.IsNullOrEmpty(existing.GetProperty("cuc").GetString()));
        Assert.Equal(owner.Cuc, existing.GetProperty("cuc").GetString());
        Assert.True(existing.GetProperty("isComplete").GetBoolean());
        Assert.True(existing.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task UpdateToAnotherCustomersDocumentReturnsTheOwnerAsExisting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var owner = await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);
        var other = await CreateCustomerAsync(client, city.CityId, classification.Id, "Naturaleza Viva", "830.222.222-2");

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{other.Id}",
            NewCustomerBody(city.CityId, classification.Id, name: "Naturaleza Viva", identificationNumber: OwnerNit),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(TakenCode, body.GetProperty("code").GetString());
        Assert.Equal(owner.Id, body.GetProperty("existing").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnInactiveOwnerComesBackWithIsActiveFalse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var owner = await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);
        var deactivated = await client.PostAsync(
            $"{CustomersUrl()}/{owner.Id}/deactivate", content: null, TestContext.Current.CancellationToken);
        deactivated.EnsureSuccessStatusCode();

        var response = await PostSecondAsync(client, city.CityId, classification.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var existing = body.GetProperty("existing");
        Assert.Equal(owner.Id, existing.GetProperty("id").GetGuid());
        Assert.False(existing.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AnIncompleteOwnerWithDocumentComesBackWithoutCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var ownerId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO customers.customers
                    (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness,
                     identification_type, identification_number)
                VALUES ('{ownerId}', '{TenantId}', '{OwnerName}', true, false, false, 1, now(), now(), 'Incomplete', 'Nit', '{OwnerNit}')
                """,
                connection);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var response = await PostSecondAsync(client, city.CityId, classification.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var existing = body.GetProperty("existing");
        Assert.Equal(ownerId, existing.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, existing.GetProperty("cuc").ValueKind);
        Assert.False(existing.GetProperty("isComplete").GetBoolean());
        Assert.True(existing.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task TheProblemKeepsCodeAndTraceIdAndHasNoErrors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(client, city.CityId, classification.Id, OwnerName, OwnerNit);

        var response = await PostSecondAsync(client, city.CityId, classification.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(TakenCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.True(body.TryGetProperty("existing", out _));
        Assert.False(body.TryGetProperty("errors", out _));
    }
}
