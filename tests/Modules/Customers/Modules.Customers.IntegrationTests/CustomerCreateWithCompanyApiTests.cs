using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-11-15 «Contacto de» en el alta: <c>POST /customers</c> con <c>companyId</c> crea el cliente ya vinculado,
/// con las mismas reglas y códigos que <c>PUT /customers/{id}/company</c>. Un Postgres por prueba, como el resto del módulo.</summary>
public sealed class CustomerCreateWithCompanyApiTests
{
    private const string NewContactName = "Nuevo Contacto";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record Arranged(HttpClient Client, string ConnectionString, Guid CityId, Guid ClassificationId);

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        return new Arranged(client, connectionString, city.CityId, classification.Id);
    }

    // `NewCustomerBody` es un anónimo: se pasa a JSON y se le suma `companyId` sólo cuando la prueba lo pide.
    private static JsonObject Body(Arranged arranged, string name, int number, Guid? companyId = null)
    {
        var body = JsonSerializer.SerializeToNode(
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, name, "CC", $"1020304{number:D3}"),
            WebJson)!.AsObject();
        if (companyId is { } id)
        {
            body["companyId"] = id;
        }

        return body;
    }

    private static Task<HttpResponseMessage> PostAsync(Arranged arranged, JsonObject body) =>
        arranged.Client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken);

    private static async Task<Guid> CreateAsync(Arranged arranged, string name, int number)
    {
        var response = await PostAsync(arranged, Body(arranged, name, number));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created.Id;
    }

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, Guid customerId, Guid? companyId) =>
        client.PutAsJsonAsync(
            $"{CustomersUrl()}/{customerId}/company", new { companyId }, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(string connectionString, string sql) =>
        long.Parse((await TextAsync(connectionString, sql))!, CultureInfo.InvariantCulture);

    private static Task<long> AuditRowsAsync(string connectionString, string action, Guid customerId) =>
        CountAsync(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages "
            + $"WHERE payload->>'action' = '{action}' AND payload->>'resourceId' = '{customerId}'");

    [Fact]
    public async Task CreatingWithACompanyReturnsTheContactLinked()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        var response = await PostAsync(arranged, Body(arranged, NewContactName, 5, company));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(company, body.GetProperty("company").GetProperty("id").GetGuid());
        Assert.Equal("Verde Esencial", body.GetProperty("company").GetProperty("name").GetString());
        var created = body.GetProperty("id").GetGuid();
        Assert.Equal(
            company.ToString(),
            await TextAsync(arranged.ConnectionString, $"SELECT company_id FROM customers.customers WHERE id = '{created}'"));
    }

    [Fact]
    public async Task CreatingWithoutACompanyLeavesItNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;

        var response = await PostAsync(arranged, Body(arranged, NewContactName, 5));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyAsync(response)).GetProperty("company").ValueKind);
    }

    [Theory]
    [InlineData("not_found", "customers.customer.company_not_found")]
    [InlineData("inactive", "customers.customer.company_inactive")]
    [InlineData("contact", "customers.customer.company_is_contact")]
    public async Task EachRuleRejectsTheCreation(string scenario, string code)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var ct = TestContext.Current.CancellationToken;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        switch (scenario)
        {
            case "not_found":
                company = Guid.CreateVersion7();
                break;
            case "inactive":
                (await client.PostAsync($"{CustomersUrl()}/{company}/deactivate", null, ct)).EnsureSuccessStatusCode();
                break;
            case "contact":
                // La empresa elegida ya es contacto de un tercero: no puede ser la empresa de nadie.
                var third = await CreateAsync(arranged, "Tercera S.A.S.", 3);
                (await LinkAsync(client, company, third)).EnsureSuccessStatusCode();
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        var response = await PostAsync(arranged, Body(arranged, NewContactName, 5, company));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, (await BodyAsync(response)).GetProperty("code").GetString());
        // Atómico: una empresa inválida no deja al cliente creado sin ella.
        Assert.Equal(
            0,
            await CountAsync(
                arranged.ConnectionString, $"SELECT count(*) FROM customers.customers WHERE name = '{NewContactName}'"));
    }

    [Fact]
    public async Task ATakenDocumentStillNamesItsOwnerWithACompany()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        var owner = await CreateAsync(arranged, "Laura Pérez", 5);

        // Mismo documento que Laura (número 5), con empresa: la transacción abierta por el bloqueo no puede tapar el 422.
        var response = await PostAsync(arranged, Body(arranged, NewContactName, 5, company));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("customers.customer.identification_taken", body.GetProperty("code").GetString());
        Assert.Equal(owner, body.GetProperty("existing").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CreatingWithACompanyAuditsOnlyTheCreation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        var response = await PostAsync(arranged, Body(arranged, NewContactName, 5, company));
        response.EnsureSuccessStatusCode();
        var created = (await BodyAsync(response)).GetProperty("id").GetGuid();

        Assert.Equal(1, await AuditRowsAsync(arranged.ConnectionString, "customers.customer.created", created));
        Assert.Equal(0, await AuditRowsAsync(arranged.ConnectionString, CustomerAuditActions.CompanyChanged, created));
    }
}
