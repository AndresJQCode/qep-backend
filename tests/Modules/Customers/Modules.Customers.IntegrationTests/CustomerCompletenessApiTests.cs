using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.2: <c>isComplete</c> en el detalle y la lista, el filtro, completar con el PUT
/// (CUC nuevo y auditoría <c>customers.customer.completed</c>), un PUT incompleto sigue siendo 422, y el export
/// con la columna «Estado de la ficha».</summary>
public sealed class CustomerCompletenessApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static async Task<Guid> SeedIncompleteAsync(QepApiFactory factory, string userId = "CO.1349120865530274", string name = "Laura Pérez")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        return (await directory.EnsureAsync(Tenant, new WhatsAppContact(userId, "+573001234567", name, null), TestContext.Current.CancellationToken)).CustomerId;
    }

    [Fact]
    public async Task TheDetailAndTheListSayWhetherTheRecordIsCompleteAndTheListFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var complete = await CreateCustomerAsync(client, city.CityId, classification.Id);
        var incomplete = await SeedIncompleteAsync(factory);

        var detail = await client.GetFromJsonAsync<JsonElement>($"{CustomersUrl()}/{incomplete}", TestContext.Current.CancellationToken);
        var onlyIncomplete = await ListAsync(client, "?isComplete=false");
        var onlyComplete = await ListAsync(client, "?isComplete=TRUE");
        var all = await ListAsync(client, string.Empty);

        Assert.False(detail.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("cuc").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("classification").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("identificationType").ValueKind);
        Assert.Equal("Laura Pérez", detail.GetProperty("name").GetString());
        Assert.Equal(incomplete, Assert.Single(onlyIncomplete.Items).Id);
        Assert.Equal(complete.Id, Assert.Single(onlyComplete.Items).Id);
        Assert.True(Assert.Single(onlyComplete.Items).IsComplete);
        Assert.Equal(2, all.Total);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public async Task AnUnknownIsCompleteIsAValidationErrorOnThatKey(string value)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);

        var response = await client.GetAsync($"{CustomersUrl()}?isComplete={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["isComplete"], await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task APutWithEverythingCompletesTheRecordWithANewCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}",
            NewCustomerBody(city.CityId, classification.Id, name: "Laura Pérez", identificationType: "CC", identificationNumber: "1020304050"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("isComplete").GetBoolean());
        Assert.StartsWith(classification.Prefix, body.GetProperty("cuc").GetString(), StringComparison.Ordinal);
        Assert.Single(body.GetProperty("addresses").EnumerateArray());
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var audit = new NpgsqlCommand(
            $"SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = '{CustomerAuditActions.Completed}' AND payload->>'resourceId' = '{incomplete}'",
            connection);
        Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        // El BSUID sobrevive a completar la ficha: la conversación sigue atada a este cliente.
        await using var bsuid = new NpgsqlCommand($"SELECT whatsapp_user_id FROM customers.customers WHERE id = '{incomplete}'", connection);
        Assert.Equal("CO.1349120865530274", (string)(await bsuid.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task AnIncompletePutIsStillTheUsualValidationError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}",
            NewCustomerBody(city.CityId, classification.Id, identificationNumber: string.Empty),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // PascalCase: es la clave que CustomerWriteRules ya emite para el formulario (customerFieldErrors).
        Assert.Contains("IdentificationNumber", await ValidationFieldsAsync(response));
        var detail = await client.GetFromJsonAsync<JsonElement>($"{CustomersUrl()}/{incomplete}", TestContext.Current.CancellationToken);
        Assert.False(detail.GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task CompletingKeepsTheWhatsAppPhoneSearchable()
    {
        // Spec 2026-10-10 §6.2: completar recalcula phone_e164 como el alta; si se perdiera, la bandeja dejaría
        // de encontrar al cliente por su número.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}",
            NewCustomerBody(city.CityId, classification.Id, phone: "300 123 4567"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var phone = new NpgsqlCommand($"SELECT phone_e164 FROM customers.customers WHERE id = '{incomplete}'", connection);
        Assert.Equal("+573001234567", (string)(await phone.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task AnIncompleteRecordGetsNoAddressUntilItIsCompleted()
    {
        // Spec 2026-10-10 §5.2: no hay edición parcial de un incompleto; la libreta nace al completarlo.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var incomplete = await SeedIncompleteAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{CustomersUrl()}/{incomplete}/addresses",
            new { name = "Bodega Norte", address = "Carrera 7 # 71-21", cityId = city.CityId, isPrincipal = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("customers.customer.incomplete", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
