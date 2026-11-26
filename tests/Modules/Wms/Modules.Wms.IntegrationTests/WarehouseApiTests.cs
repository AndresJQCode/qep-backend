using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Alta y edición de bodegas (RF-001, RF-003, RF-102) de punta a punta por HTTP.</summary>
public sealed class WarehouseApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WarehousesUrl => $"{WmsUrl()}/warehouses";

    private static HttpRequestMessage Write(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string code, string name, Guid? cityId = null)
    {
        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code, name, cityId }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await BodyAsync(response)).RootElement.Clone();
    }

    /// <summary>Las ubicaciones por SQL: el GET de ubicaciones llega con la Task 9.</summary>
    private static async Task<List<(string Code, bool IsVirtual)>> LocationsAsync(QepApiFactory factory, Guid warehouseId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT code, is_virtual FROM wms.locations WHERE warehouse_id = @id", connection);
        command.Parameters.AddWithValue("id", warehouseId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, bool)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return rows;
    }

    [Fact]
    public async Task CreatingAWarehouseReturns201WithItsVirtualsAndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code = "bog", name = "Bogotá" }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetGuid();
        Assert.Equal("BOG", root.GetProperty("code").GetString());
        Assert.Equal(2, root.GetProperty("locationCount").GetInt32());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.Equal($"/api/v1/tenants/{TenantId}/wms/warehouses/{id}", response.Headers.Location?.ToString());

        var locations = await LocationsAsync(factory, id);
        Assert.Equal(6, locations.Count);
        Assert.Equal(4, locations.Count(location => location.IsVirtual));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.warehouse.created", id.ToString()));
    }

    [Fact]
    public async Task ADuplicateCodeIs422CodeTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        await CreateAsync(client, "BOG", "Bogotá");

        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code = "bog", name = "Otra" }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.warehouse.code_taken", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnInvalidCodeIs422CodeInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code = "BO G", name = "Bogotá" }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.warehouse.code_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnEmptyNameIs422ValidationFailedWithTheField()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code = "BOG", name = "" }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Name", out var nameErrors));
        Assert.NotEqual(JsonValueKind.Null, nameErrors.ValueKind);
    }

    [Fact]
    public async Task AnUnknownCityIs422CityUnknown()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Write(HttpMethod.Post, WarehousesUrl, new { code = "BOG", name = "Bogotá", cityId = Guid.NewGuid() }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.warehouse.city_unknown", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AKnownCityComesBackWithItsName()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var cityId = await AnyCityIdAsync(factory);

        var created = await CreateAsync(client, "BOG", "Bogotá", cityId);

        Assert.Equal(cityId, created.GetProperty("cityId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("cityName").GetString()));
    }

    [Fact]
    public async Task UpdatingWithAStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var url = $"{WarehousesUrl}/{created.GetProperty("id").GetGuid()}";

        var response = await client.SendAsync(
            Write(HttpMethod.Put, url, new { name = "Nueva", cityId = (Guid?)null, isActive = true, version = 99 }), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task UpdatingRenamesDeactivatesAndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();

        var response = await client.SendAsync(
            Write(HttpMethod.Put, $"{WarehousesUrl}/{id}", new { name = "Bogotá Norte", cityId = (Guid?)null, isActive = false, version = 1 }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var updated = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("Bogotá Norte", updated.RootElement.GetProperty("name").GetString());
        Assert.False(updated.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(2, updated.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.warehouse.updated", id.ToString()));

        using var active = JsonDocument.Parse(await BodyAsync(await client.GetAsync(WarehousesUrl, Ct)));
        Assert.Equal(0, active.RootElement.GetProperty("items").GetArrayLength());
        using var all = JsonDocument.Parse(await BodyAsync(await client.GetAsync($"{WarehousesUrl}?includeInactive=true", Ct)));
        Assert.Equal(1, all.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnUnknownWarehouseIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Write(HttpMethod.Put, $"{WarehousesUrl}/{Guid.NewGuid()}", new { name = "X", cityId = (Guid?)null, isActive = true, version = 1 }), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.warehouse.not_found", await CodeOfAsync(response));
    }
}
