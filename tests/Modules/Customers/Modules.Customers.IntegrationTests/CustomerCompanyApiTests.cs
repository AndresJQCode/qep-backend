using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-11-10 contactos de empresa §4.1–§4.2: <c>PUT /customers/{id}/company</c> contra la base. Un Postgres por
/// prueba, como el resto del módulo.</summary>
public sealed class CustomerCompanyApiTests
{
    private const string CompanyChangedAction = CustomerAuditActions.CompanyChanged;

    // La segunda de dos vinculaciones opuestas ve la primera ya confirmada: según el lado, la empresa ya es contacto o el cliente ya
    // tiene contactos. El agregado revisa lo primero antes que lo segundo.
    private static readonly string[] OppositeLinkCodes =
        ["customers.customer.company_is_contact", "customers.customer.has_contacts"];

    private sealed record Arranged(
        HttpClient Client, string ConnectionString, Guid CityId, Guid ClassificationId, QepApiFactory Factory);

    private static async Task<Arranged> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        return new Arranged(client, connectionString, city.CityId, classification.Id, factory);
    }

    private static async Task<Guid> CreateAsync(Arranged arranged, string name, int number)
    {
        var response = await arranged.Client.PostAsJsonAsync(
            CustomersUrl(),
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, name, "CC", $"1020304{number:D3}"),
            TestContext.Current.CancellationToken);
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

    private static async Task<long> AuditRowsAsync(string connectionString, Guid customerId) =>
        long.Parse(
            (await TextAsync(
                connectionString,
                "SELECT count(*) FROM platform.outbox_messages "
                + $"WHERE payload->>'action' = '{CompanyChangedAction}' AND payload->>'resourceId' = '{customerId}'"))!,
            CultureInfo.InvariantCulture);

    private static Task<string?> StoredCompanyAsync(string connectionString, Guid customerId) =>
        TextAsync(connectionString, $"SELECT company_id FROM customers.customers WHERE id = '{customerId}'");

    private static async Task<long> VersionAsync(string connectionString, Guid customerId) =>
        long.Parse(
            (await TextAsync(connectionString, $"SELECT version FROM customers.customers WHERE id = '{customerId}'"))!,
            CultureInfo.InvariantCulture);

    [Fact]
    public async Task LinkingReturnsTheCustomerWithItsCompany()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        var response = await LinkAsync(client, person, company);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(company, body.GetProperty("company").GetProperty("id").GetGuid());
        Assert.Equal("Verde Esencial", body.GetProperty("company").GetProperty("name").GetString());
        var read = await GetAsync(client, person);
        Assert.Equal(company, read.Company?.Id);
    }

    [Fact]
    public async Task UnlinkingReturnsCompanyNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        (await LinkAsync(client, person, company)).EnsureSuccessStatusCode();

        var response = await LinkAsync(client, person, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("company").ValueKind);
        Assert.Null(await StoredCompanyAsync(arranged.ConnectionString, person));
    }

    [Fact]
    public async Task WithoutManagePermissionItIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var manager = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        using var reader = CreateClient(factory, SubjectId, TenantId, Modules.Customers.Application.CustomersPermissions.CustomerRead);

        var response = await LinkAsync(reader, person, company);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredCompanyAsync(arranged.ConnectionString, person));
    }

    [Fact]
    public async Task AnUnknownCustomerIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        var response = await LinkAsync(client, Guid.CreateVersion7(), company);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not_found", "customers.customer.company_not_found")]
    [InlineData("inactive", "customers.customer.company_inactive")]
    [InlineData("self", "customers.customer.company_is_self")]
    [InlineData("contact", "customers.customer.company_is_contact")]
    [InlineData("has_contacts", "customers.customer.has_contacts")]
    public async Task EachRuleAnswers422WithItsCode(string scenario, string code)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var ct = TestContext.Current.CancellationToken;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var other = await CreateAsync(arranged, "Verde Esencial", 2);

        Guid customer = person;
        Guid target = other;
        switch (scenario)
        {
            case "not_found":
                target = Guid.CreateVersion7();
                break;
            case "inactive":
                (await client.PostAsync($"{CustomersUrl()}/{other}/deactivate", null, ct)).EnsureSuccessStatusCode();
                break;
            case "self":
                target = person;
                break;
            case "contact":
                // `other` ya es contacto de un tercero: no puede ser la empresa de nadie.
                var third = await CreateAsync(arranged, "Tercera S.A.S.", 3);
                (await LinkAsync(client, other, third)).EnsureSuccessStatusCode();
                break;
            case "has_contacts":
                // `person` ya tiene un contacto: no puede volverse contacto.
                var contactOfPerson = await CreateAsync(arranged, "Pedro Gómez", 3);
                (await LinkAsync(client, contactOfPerson, person)).EnsureSuccessStatusCode();
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        var response = await LinkAsync(client, customer, target);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, (await BodyAsync(response)).GetProperty("code").GetString());
        Assert.Null(await StoredCompanyAsync(arranged.ConnectionString, person));
    }

    [Fact]
    public async Task TheRegularPutKeepsTheLink()
    {
        // Spec §4.2 (obligatoria): PUT /customers/{id} no lee ni escribe CompanyId.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        (await LinkAsync(client, person, company)).EnsureSuccessStatusCode();

        var put = await client.PutAsJsonAsync(
            $"{CustomersUrl()}/{person}",
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, "Laura Pérez Gómez", "CC", "1020304001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(company, (await BodyAsync(put)).GetProperty("company").GetProperty("id").GetGuid());
        Assert.Equal(company.ToString(), await StoredCompanyAsync(arranged.ConnectionString, person));
    }

    [Fact]
    public async Task TheRegularPostIgnoresACompanyIdInTheBody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        var body = System.Text.Json.JsonSerializer.SerializeToNode(
            NewCustomerBody(arranged.CityId, arranged.ClassificationId, "Laura Pérez", "CC", "1020304001"))!.AsObject();
        body["companyId"] = company;

        var response = await client.PostAsJsonAsync(CustomersUrl(), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyAsync(response);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("company").ValueKind);
        Assert.Null(await StoredCompanyAsync(arranged.ConnectionString, created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task LinkingTwiceKeepsTheVersionAndWritesOneAuditRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);

        (await LinkAsync(client, person, company)).EnsureSuccessStatusCode();
        var version = await VersionAsync(arranged.ConnectionString, person);
        var second = await LinkAsync(client, person, company);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(version, await VersionAsync(arranged.ConnectionString, person));
        Assert.Equal(1L, await AuditRowsAsync(arranged.ConnectionString, person));
    }

    [Fact]
    public async Task TwoOppositeLinksAtOnceNeverLeaveTwoLevels()
    {
        // G2 / diferido de la revisión de la tarea 2: A→B y B→A en paralelo se serializan por LockForCompanyLinkAsync. Si no, las
        // dos transacciones leerían «B no tiene empresa» y «A no tiene empresa», y las dos confirmarían: dos niveles.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var a = await CreateAsync(arranged, "Ana Ruiz", 1);
        var b = await CreateAsync(arranged, "Bruno Díaz", 2);

        var responses = await Task.WhenAll(LinkAsync(client, a, b), LinkAsync(client, b, a));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Contains(
            (await BodyAsync(rejected)).GetProperty("code").GetString(),
            OppositeLinkCodes);
        var linked = await TextAsync(
            arranged.ConnectionString,
            $"SELECT count(*) FROM customers.customers WHERE id IN ('{a}', '{b}') AND company_id IS NOT NULL");
        Assert.Equal("1", linked);
    }

    [Fact]
    public async Task TheAuditRowIsWrittenInTheSameTransaction()
    {
        // La fila de auditoría y el vínculo se confirman juntos o ninguno: una escritura que sale bien deja las dos, y una que el
        // agregado rechaza no deja ninguna. Se mide el resultado observable en la base, no el orden de las llamadas.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = arranged.Client;
        var person = await CreateAsync(arranged, "Laura Pérez", 1);
        var company = await CreateAsync(arranged, "Verde Esencial", 2);
        var inactive = await CreateAsync(arranged, "Cerrada S.A.S.", 3);
        (await client.PostAsync($"{CustomersUrl()}/{inactive}/deactivate", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var rejected = await LinkAsync(client, person, inactive);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Null(await StoredCompanyAsync(arranged.ConnectionString, person));
        Assert.Equal(0L, await AuditRowsAsync(arranged.ConnectionString, person));

        (await LinkAsync(client, person, company)).EnsureSuccessStatusCode();
        Assert.Equal(company.ToString(), await StoredCompanyAsync(arranged.ConnectionString, person));
        Assert.Equal(1L, await AuditRowsAsync(arranged.ConnectionString, person));
    }
}
