using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>CD-04: <c>GET /customers/anomalies</c> agrupa los clientes que comparten celular o número de documento. Incluye
/// inactivos e incompletos, ordena cada grupo por <c>created_at, id</c> y pagina grupos, no clientes.</summary>
public sealed class CustomerAnomaliesApiTests
{
    private const string SharedPhone = "+573043549660";

    private static string AnomaliesUrl(string query, string tenantId = TenantId) =>
        $"{CustomersUrl(tenantId)}/anomalies{query}";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private sealed record Seed(
        string Name,
        string? Phone = null,
        string? Type = null,
        string? Number = null,
        bool Active = true,
        bool Complete = true,
        int AgeMinutes = 0,
        string Tenant = TenantId);

    private static async Task<Guid> InsertAsync(string connectionString, Seed seed)
    {
        var id = Guid.NewGuid();
        // Una ficha completa exige CUC, documento, dirección y país (CK_customers_complete_fields): si la prueba no pide
        // documento, se le da uno que no se repita para que no arme otro grupo.
        var type = seed.Type ?? (seed.Complete ? "Nit" : null);
        var number = seed.Number ?? (seed.Complete ? id.ToString("N")[..30] : null);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers
                (id, tenant_id, name, is_active, with_retention, vat_surplus, version, created_at, updated_at, completeness,
                 identification_type, identification_number, phone, phone_e164, cuc, address, country)
            VALUES (@id, @tenant, @name, @active, false, false, 1, now() - make_interval(mins => @age), now(), @completeness,
                    @type, @number, @phone, @phone, @cuc, @address, @country)
            """,
            connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", Guid.Parse(seed.Tenant));
        insert.Parameters.AddWithValue("name", seed.Name);
        insert.Parameters.AddWithValue("active", seed.Active);
        insert.Parameters.AddWithValue("age", seed.AgeMinutes);
        insert.Parameters.AddWithValue("completeness", seed.Complete ? "Complete" : "Incomplete");
        insert.Parameters.AddWithValue("type", (object?)type ?? DBNull.Value);
        insert.Parameters.AddWithValue("number", (object?)number ?? DBNull.Value);
        insert.Parameters.AddWithValue("cuc", seed.Complete ? $"C{id:N}"[..16] : DBNull.Value);
        insert.Parameters.AddWithValue("address", seed.Complete ? "Calle 1" : DBNull.Value);
        insert.Parameters.AddWithValue("country", seed.Complete ? "CO" : DBNull.Value);
        insert.Parameters.AddWithValue("phone", (object?)seed.Phone ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    [Fact]
    public async Task SharedPhoneGroupsTheCustomersOfANumberOldestFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        var newest = await InsertAsync(connectionString, new Seed("Tercero", SharedPhone, AgeMinutes: 10));
        var oldest = await InsertAsync(connectionString, new Seed("Primero", SharedPhone, Complete: false, AgeMinutes: 30));
        var middle = await InsertAsync(connectionString, new Seed("Segundo", SharedPhone, Active: false, AgeMinutes: 20));
        await InsertAsync(connectionString, new Seed("Pareja A", "+573001112233"));
        await InsertAsync(connectionString, new Seed("Pareja B", "+573001112233"));
        await InsertAsync(connectionString, new Seed("Solo", "+573009998877"));

        var response = await client.GetAsync(AnomaliesUrl("?kind=SharedPhone"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        var groups = body.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal(SharedPhone, groups[0].GetProperty("key").GetString());
        Assert.False(groups[0].GetProperty("truncated").GetBoolean());
        var customers = groups[0].GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal([oldest, middle, newest], customers.Select(customer => customer.GetProperty("id").GetGuid()).ToArray());
        Assert.False(customers[0].GetProperty("isComplete").GetBoolean());
        Assert.False(customers[1].GetProperty("isActive").GetBoolean());
        Assert.Equal(SharedPhone, customers[0].GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, customers[0].GetProperty("identificationNumber").ValueKind);
        Assert.Equal(2, groups[1].GetProperty("customers").GetArrayLength());
    }

    [Fact]
    public async Task SharedIdentificationNumberGroupsAcrossTypesAndFormats()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        var first = await InsertAsync(connectionString, new Seed("Con Nit", Type: "Nit", Number: "92-1396892", AgeMinutes: 20));
        var second = await InsertAsync(connectionString, new Seed("Con CE", Type: "Ce", Number: "921396892", AgeMinutes: 10));
        await InsertAsync(connectionString, new Seed("Otro", Type: "Nit", Number: "830.222.222-2"));
        await InsertAsync(connectionString, new Seed("WhatsApp sin documento", Complete: false));
        await InsertAsync(connectionString, new Seed("WhatsApp sin documento 2", Complete: false));

        var response = await client.GetAsync(
            AnomaliesUrl("?kind=SharedIdentificationNumber"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        var group = Assert.Single(body.GetProperty("groups").EnumerateArray());
        Assert.Equal("921396892", group.GetProperty("key").GetString());
        Assert.Equal(
            [first, second],
            group.GetProperty("customers").EnumerateArray().Select(customer => customer.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("NIT", group.GetProperty("customers")[0].GetProperty("identificationType").GetString());
        Assert.Equal("CE", group.GetProperty("customers")[1].GetProperty("identificationType").GetString());
        var counts = body.GetProperty("counts").EnumerateArray()
            .ToDictionary(count => count.GetProperty("kind").GetString()!, count => count.GetProperty("groups").GetInt32());
        Assert.Equal(1, counts["SharedIdentificationNumber"]);
        Assert.Equal(0, counts["SharedPhone"]);
    }

    [Fact]
    public async Task TheGroupsArePagedNotTheCustomers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        foreach (var (phone, size) in new[] { ("+573001000001", 4), ("+573001000002", 3), ("+573001000003", 2) })
        {
            for (var i = 0; i < size; i++)
            {
                await InsertAsync(connectionString, new Seed($"{phone} #{i}", phone));
            }
        }

        var second = await client.GetAsync(
            AnomaliesUrl("?kind=SharedPhone&page=2&pageSize=2"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await BodyAsync(second);
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
        var group = Assert.Single(body.GetProperty("groups").EnumerateArray());
        Assert.Equal("+573001000003", group.GetProperty("key").GetString());
        Assert.Equal(2, group.GetProperty("customers").GetArrayLength());
    }

    [Fact]
    public async Task AGroupOverFiftyIsCutAndFlagged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        for (var i = 0; i < 52; i++)
        {
            await InsertAsync(connectionString, new Seed($"Cliente {i}", SharedPhone, AgeMinutes: 100 - i));
        }

        var response = await client.GetAsync(AnomaliesUrl("?kind=SharedPhone"), TestContext.Current.CancellationToken);

        var group = Assert.Single((await BodyAsync(response)).GetProperty("groups").EnumerateArray());
        Assert.Equal(50, group.GetProperty("customers").GetArrayLength());
        Assert.True(group.GetProperty("truncated").GetBoolean());
        Assert.Equal("Cliente 0", group.GetProperty("customers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnknownOrMissingKindIs422OnKind()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var unknown = await client.GetAsync(AnomaliesUrl("?kind=Phone"), TestContext.Current.CancellationToken);
        var missing = await client.GetAsync(AnomaliesUrl(string.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("Kind", await ValidationFieldsAsync(unknown));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("Kind", await ValidationFieldsAsync(missing));
    }

    [Fact]
    public async Task AHugePageIs422OnPageInsteadOfAnOverflowingOffset()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.GetAsync(
            AnomaliesUrl("?kind=SharedPhone&page=2147483647"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Page", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task AnotherTenantIs403AndItsGroupsNeverShow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        using var noPermission = CreateClient(factory, SubjectId, TenantId);
        await EnsureCityAsync(client);
        var connectionString = database.GetConnectionString();
        await InsertAsync(connectionString, new Seed("Ajeno 1", SharedPhone, Tenant: OtherTenantId));
        await InsertAsync(connectionString, new Seed("Ajeno 2", SharedPhone, Tenant: OtherTenantId));

        var foreign = await client.GetAsync(
            AnomaliesUrl("?kind=SharedPhone", OtherTenantId), TestContext.Current.CancellationToken);
        var own = await client.GetAsync(AnomaliesUrl("?kind=SharedPhone"), TestContext.Current.CancellationToken);
        var withoutRead = await noPermission.GetAsync(AnomaliesUrl("?kind=SharedPhone"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutRead.StatusCode);
        var body = await BodyAsync(own);
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Empty(body.GetProperty("groups").EnumerateArray());
        Assert.All(body.GetProperty("counts").EnumerateArray(), count => Assert.Equal(0, count.GetProperty("groups").GetInt32()));
        Assert.Equal(2, body.GetProperty("counts").GetArrayLength());
        Assert.Equal("SharedPhone", body.GetProperty("counts")[0].GetProperty("kind").GetString());
    }
}
