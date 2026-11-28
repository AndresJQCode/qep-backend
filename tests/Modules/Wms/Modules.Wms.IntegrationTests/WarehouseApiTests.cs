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

    private static string PrincipalUrl(Guid warehouseId, string tenantId = TenantId) =>
        $"{WmsUrl(tenantId)}/warehouses/{warehouseId}/principal";

    private static async Task<JsonElement> WarehouseAsync(HttpClient client, Guid warehouseId)
    {
        using var all = JsonDocument.Parse(await BodyAsync(await client.GetAsync($"{WarehousesUrl}?includeInactive=true", Ct)));
        return all.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == warehouseId).Clone();
    }

    [Fact]
    public async Task SettingThePrincipalMovesTheFlag()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var first = await CreateAsync(client, "BOG", "Bogotá");
        var second = await CreateAsync(client, "MED", "Medellín");
        var firstId = first.GetProperty("id").GetGuid();
        var secondId = second.GetProperty("id").GetGuid();
        Assert.False(first.GetProperty("isPrincipal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("generalLocationId").ValueKind);

        var response = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(firstId), new { version = 1 }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var principal = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal(firstId, principal.RootElement.GetProperty("id").GetGuid());
        Assert.True(principal.RootElement.GetProperty("isPrincipal").GetBoolean());
        var generalId = principal.RootElement.GetProperty("generalLocationId").GetGuid();
        Assert.Equal(
            "GENERAL",
            await ScalarAsync<string>(database, "SELECT code FROM wms.locations WHERE id = @id AND warehouse_id = @w", ("id", generalId), ("w", firstId)));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.warehouse.updated", firstId.ToString()));

        // Elegir otra mueve la marca: la anterior queda sin ella, la nueva gana su ubicación general.
        var moved = await client.SendAsync(
            Write(HttpMethod.Put, PrincipalUrl(secondId), new { version = second.GetProperty("version").GetInt64() }), Ct);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var firstNow = await WarehouseAsync(client, firstId);
        var secondNow = await WarehouseAsync(client, secondId);
        Assert.False(firstNow.GetProperty("isPrincipal").GetBoolean());
        Assert.True(secondNow.GetProperty("isPrincipal").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, secondNow.GetProperty("generalLocationId").ValueKind);
        // La anterior conserva su ubicación general: no se borra nada.
        Assert.Equal(generalId, firstNow.GetProperty("generalLocationId").GetGuid());
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.warehouses WHERE is_principal"));
    }

    [Fact]
    public async Task SettingThePrincipalTwiceChangesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();
        var first = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version = 1 }), Ct);
        using var firstBody = JsonDocument.Parse(await BodyAsync(first));
        var version = firstBody.RootElement.GetProperty("version").GetInt64();

        var again = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version }), Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var againBody = JsonDocument.Parse(await BodyAsync(again));
        Assert.Equal(version, againBody.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.warehouse.updated", id.ToString()));
    }

    [Fact]
    public async Task SettingThePrincipalWithAStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();

        var response = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version = 99 }), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.False((await WarehouseAsync(client, id)).GetProperty("isPrincipal").GetBoolean());
    }

    [Fact]
    public async Task SettingThePrincipalOfAnotherTenantIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        // Quien está activo en otro tenant llama a la ruta de éste: 403 y no 404, que confirmaría que el id existe acá.
        var response = await intruder.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version = 1 }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
        Assert.False((await WarehouseAsync(client, id)).GetProperty("isPrincipal").GetBoolean());
    }

    [Fact]
    public async Task SettingThePrincipalWithoutPermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();
        using var reader = CreateReader(factory);

        var response = await reader.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version = 1 }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SettingAnInactiveWarehouseAsPrincipalIs422AndChangesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var first = await CreateAsync(client, "BOG", "Bogotá");
        var firstId = first.GetProperty("id").GetGuid();
        var principal = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(firstId), new { version = 1 }), Ct);
        Assert.Equal(HttpStatusCode.OK, principal.StatusCode);
        var second = await CreateAsync(client, "MED", "Medellín");
        var secondId = second.GetProperty("id").GetGuid();
        var deactivated = await client.SendAsync(
            Write(HttpMethod.Put, $"{WarehousesUrl}/{secondId}", new { name = "Medellín", cityId = (Guid?)null, isActive = false, version = 1 }), Ct);
        using var body = JsonDocument.Parse(await BodyAsync(deactivated));
        var version = body.RootElement.GetProperty("version").GetInt64();

        var response = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(secondId), new { version }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.warehouse.inactive_cannot_be_principal", await CodeOfAsync(response));
        // La anterior sigue siendo la principal: la transacción se deshizo entera.
        Assert.True((await WarehouseAsync(client, firstId)).GetProperty("isPrincipal").GetBoolean());
        Assert.False((await WarehouseAsync(client, secondId)).GetProperty("isPrincipal").GetBoolean());
    }

    [Fact]
    public async Task DeactivatingThePrincipalIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateAsync(client, "BOG", "Bogotá");
        var id = created.GetProperty("id").GetGuid();
        var principal = await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(id), new { version = 1 }), Ct);
        using var body = JsonDocument.Parse(await BodyAsync(principal));
        var version = body.RootElement.GetProperty("version").GetInt64();

        var response = await client.SendAsync(
            Write(HttpMethod.Put, $"{WarehousesUrl}/{id}", new { name = "Bogotá", cityId = (Guid?)null, isActive = false, version }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.warehouse.principal_cannot_be_deactivated", await CodeOfAsync(response));
        Assert.True(await ScalarAsync<bool>(database, "SELECT is_active FROM wms.warehouses WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task ListingWarehousesShowsIsPrincipal()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var first = await CreateAsync(client, "BOG", "Bogotá");
        await CreateAsync(client, "MED", "Medellín");
        var firstId = first.GetProperty("id").GetGuid();
        await client.SendAsync(Write(HttpMethod.Put, PrincipalUrl(firstId), new { version = 1 }), Ct);

        using var all = JsonDocument.Parse(await BodyAsync(await client.GetAsync(WarehousesUrl, Ct)));

        var items = all.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["BOG"], items.Where(item => item.GetProperty("isPrincipal").GetBoolean()).Select(item => item.GetProperty("code").GetString()));
        var principal = items.Single(item => item.GetProperty("isPrincipal").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, principal.GetProperty("generalLocationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, items.Single(item => !item.GetProperty("isPrincipal").GetBoolean()).GetProperty("generalLocationId").ValueKind);
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
