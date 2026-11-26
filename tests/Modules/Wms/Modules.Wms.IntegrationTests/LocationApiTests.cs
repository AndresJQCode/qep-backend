using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>El árbol de ubicaciones (RF-002, RF-003, §8, RF-102) y la lista escaneable con su etag, por HTTP.</summary>
public sealed class LocationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WarehousesUrl => $"{WmsUrl()}/warehouses";

    private static HttpRequestMessage Write(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    private static async Task<Guid> CreateWarehouseAsync(HttpClient client, string code = "BOG")
    {
        var response = await client.SendAsync(Write(HttpMethod.Post, WarehousesUrl, new { code, name = "Bodega" }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static string LocationsUrl(Guid warehouseId) => $"{WarehousesUrl}/{warehouseId}/locations";

    private static object NewBody(
        string code,
        string kind,
        string role,
        Guid? parentId = null,
        bool allowsStorage = true,
        bool allowsPicking = false,
        int putawayPriority = 0) => new
    {
        parentId,
        code,
        kind,
        role,
        allowsStorage,
        allowsPicking,
        maxWeightKg = (decimal?)null,
        maxVolumeM3 = (decimal?)null,
        maxUnits = (int?)null,
        putawayPriority,
        storageClasses = new[] { "SECO" },
    };

    private static object PutBody(JsonElement location, string? status = null, Guid? parentId = null, bool moveParent = false) => new
    {
        parentId = moveParent ? parentId : location.GetProperty("parentId").ValueKind == JsonValueKind.Null ? (Guid?)null : location.GetProperty("parentId").GetGuid(),
        code = location.GetProperty("code").GetString(),
        kind = location.GetProperty("kind").GetString(),
        role = location.GetProperty("role").GetString(),
        allowsStorage = location.GetProperty("allowsStorage").GetBoolean(),
        allowsPicking = location.GetProperty("allowsPicking").GetBoolean(),
        maxWeightKg = (decimal?)null,
        maxVolumeM3 = (decimal?)null,
        maxUnits = (int?)null,
        putawayPriority = location.GetProperty("putawayPriority").GetInt32(),
        storageClasses = location.GetProperty("storageClasses").EnumerateArray().Select(item => item.GetString()).ToArray(),
        status = status ?? location.GetProperty("status").GetString(),
        version = location.GetProperty("version").GetInt64(),
    };

    private static async Task<JsonElement> PostLocationAsync(HttpClient client, Guid warehouseId, object body)
    {
        var response = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), body), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await BodyAsync(response)).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Guid warehouseId)
    {
        var response = await client.GetAsync(LocationsUrl(warehouseId), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private static JsonElement ByCode(List<JsonElement> items, string code) =>
        items.Single(item => item.GetProperty("code").GetString() == code);

    private static async Task<HttpResponseMessage> ScannableAsync(HttpClient client, Guid warehouseId, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{LocationsUrl(warehouseId)}/scannable");
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task ListingReturnsTheFlatTreeWithParentIdsAndPathsOrderedByPath()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var items = await ListAsync(client, warehouseId);

        Assert.Equal(6, items.Count);
        var paths = items.Select(item => item.GetProperty("path").GetString()!).ToList();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
        Assert.Equal("BOG/RECEIVING/REC-01", ByCode(items, "REC-01").GetProperty("path").GetString());
        Assert.Equal(
            ByCode(items, "RECEIVING").GetProperty("id").GetGuid(),
            ByCode(items, "REC-01").GetProperty("parentId").GetGuid());
        Assert.Equal("BOG/SUPPLIER", ByCode(items, "SUPPLIER").GetProperty("path").GetString());
        Assert.True(ByCode(items, "SUPPLIER").GetProperty("isVirtual").GetBoolean());
    }

    [Fact]
    public async Task CreatingAZoneAndABinBuildsThePathAndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var zone = await PostLocationAsync(client, warehouseId, NewBody("a", "Zone", "Storage", allowsStorage: false));
        var bin = await PostLocationAsync(
            client, warehouseId, NewBody("A-01", "Bin", "Picking", zone.GetProperty("id").GetGuid(), allowsPicking: true, putawayPriority: 3));

        Assert.Equal("BOG/A", zone.GetProperty("path").GetString());
        Assert.Equal("BOG/A/A-01", bin.GetProperty("path").GetString());
        Assert.Equal("Bin", bin.GetProperty("kind").GetString());
        Assert.Equal("Active", bin.GetProperty("status").GetString());
        Assert.Equal(1, bin.GetProperty("version").GetInt64());
        Assert.Equal("SECO", bin.GetProperty("storageClasses")[0].GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.location.created", bin.GetProperty("id").GetGuid().ToString()));
    }

    [Fact]
    public async Task ADuplicateCodeInTheWarehouseIs422CodeTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));

        var response = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), NewBody("a", "Zone", "Storage")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.code_taken", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AVirtualKindOrRoleIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var kind = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), NewBody("V1", "Virtual", "Storage")), Ct);
        var role = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), NewBody("Z1", "Zone", "Transit")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, kind.StatusCode);
        Assert.Equal("wms.location.kind_not_allowed", await CodeOfAsync(kind));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, role.StatusCode);
        Assert.Equal("wms.location.role_not_allowed", await CodeOfAsync(role));
    }

    [Fact]
    public async Task AParentFromAnotherWarehouseIs422ParentInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var first = await CreateWarehouseAsync(client, "BOG");
        var second = await CreateWarehouseAsync(client, "MED");
        var foreignZone = await PostLocationAsync(client, second, NewBody("A", "Zone", "Storage"));

        var response = await client.SendAsync(
            Write(HttpMethod.Post, LocationsUrl(first), NewBody("R1", "Rack", "Storage", foreignZone.GetProperty("id").GetGuid())), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.parent_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task MovingALocationUpdatesTheChildrenPaths()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var zoneA = await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));
        var zoneB = await PostLocationAsync(client, warehouseId, NewBody("B", "Zone", "Storage"));
        var rack = await PostLocationAsync(client, warehouseId, NewBody("R1", "Rack", "Storage", zoneA.GetProperty("id").GetGuid()));
        var bin = await PostLocationAsync(client, warehouseId, NewBody("R1-01", "Bin", "Storage", rack.GetProperty("id").GetGuid()));

        var response = await client.SendAsync(
            Write(
                HttpMethod.Put,
                $"{LocationsUrl(warehouseId)}/{rack.GetProperty("id").GetGuid()}",
                PutBody(rack, parentId: zoneB.GetProperty("id").GetGuid(), moveParent: true)),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await ListAsync(client, warehouseId);
        Assert.Equal("BOG/B/R1", ByCode(items, "R1").GetProperty("path").GetString());
        Assert.Equal("BOG/B/R1/R1-01", ByCode(items, "R1-01").GetProperty("path").GetString());
        Assert.Equal(bin.GetProperty("version").GetInt64(), ByCode(items, "R1-01").GetProperty("version").GetInt64());
        Assert.Equal(2, ByCode(items, "R1").GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.location.updated", rack.GetProperty("id").GetGuid().ToString()));
    }

    [Fact]
    public async Task UpdatingAStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var zone = await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));
        var first = await client.SendAsync(
            Write(HttpMethod.Put, $"{LocationsUrl(warehouseId)}/{zone.GetProperty("id").GetGuid()}", PutBody(zone, "Blocked")), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // La misma versión 1 otra vez: ya no es la actual.
        var stale = await client.SendAsync(
            Write(HttpMethod.Put, $"{LocationsUrl(warehouseId)}/{zone.GetProperty("id").GetGuid()}", PutBody(zone, "Active")), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task AnUnknownLocationIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var zone = await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));

        var put = await client.SendAsync(Write(HttpMethod.Put, $"{LocationsUrl(warehouseId)}/{Guid.NewGuid()}", PutBody(zone)), Ct);
        var delete = await client.SendAsync(Write(HttpMethod.Delete, $"{LocationsUrl(warehouseId)}/{Guid.NewGuid()}"), Ct);
        var list = await client.GetAsync(LocationsUrl(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("wms.location.not_found", await CodeOfAsync(put));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("wms.location.not_found", await CodeOfAsync(delete));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal("wms.warehouse.not_found", await CodeOfAsync(list));
    }

    [Fact]
    public async Task UpdatingAVirtualLocationIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var transit = ByCode(await ListAsync(client, warehouseId), "TRANSIT");
        var url = $"{LocationsUrl(warehouseId)}/{transit.GetProperty("id").GetGuid()}";

        // Cuerpo idéntico al GET: también se rechaza (S22).
        var identical = await client.SendAsync(Write(HttpMethod.Put, url, PutBody(transit)), Ct);
        var blocked = await client.SendAsync(Write(HttpMethod.Put, url, PutBody(transit, "Blocked")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, identical.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(identical));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(blocked));
        Assert.Equal(transit.GetProperty("version").GetInt64(), ByCode(await ListAsync(client, warehouseId), "TRANSIT").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AnInvalidBodyIs422ValidationFailedWithTheField()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var response = await client.SendAsync(
            Write(HttpMethod.Post, LocationsUrl(warehouseId), NewBody("", "Zone", "Storage", putawayPriority: -1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var errors = document.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Code", out var codeErrors), "errors.Code");
        Assert.True(errors.TryGetProperty("PutawayPriority", out var priorityErrors), "errors.PutawayPriority");
    }

    [Fact]
    public async Task AnUnknownKindIsAFieldErrorNeverA500()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var response = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), NewBody("A", "1", "Storage")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Kind", out var kindErrors));
    }

    [Fact]
    public async Task DeletingAVirtualIs422AndDeletingAnEmptyBinIs204()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var zone = await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));
        var bin = await PostLocationAsync(client, warehouseId, NewBody("A-01", "Bin", "Storage", zone.GetProperty("id").GetGuid()));
        var supplier = ByCode(await ListAsync(client, warehouseId), "SUPPLIER");

        var virtualDelete = await client.SendAsync(
            Write(HttpMethod.Delete, $"{LocationsUrl(warehouseId)}/{supplier.GetProperty("id").GetGuid()}"), Ct);
        var binId = bin.GetProperty("id").GetGuid();
        var binDelete = await client.SendAsync(Write(HttpMethod.Delete, $"{LocationsUrl(warehouseId)}/{binId}"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, virtualDelete.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(virtualDelete));
        Assert.Equal(HttpStatusCode.NoContent, binDelete.StatusCode);
        Assert.DoesNotContain(await ListAsync(client, warehouseId), item => item.GetProperty("id").GetGuid() == binId);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.location.deleted", binId.ToString()));
    }

    [Theory]
    [InlineData("RECEIVING")]
    [InlineData("REC-01")]
    public async Task TheReceivingZoneAndItsDockCannotBeDeleted(string code)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var location = ByCode(await ListAsync(client, warehouseId), code);

        var response = await client.SendAsync(
            Write(HttpMethod.Delete, $"{LocationsUrl(warehouseId)}/{location.GetProperty("id").GetGuid()}"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.virtual_immutable", await CodeOfAsync(response));
        Assert.Contains(await ListAsync(client, warehouseId), item => item.GetProperty("code").GetString() == code);
    }

    [Theory]
    [InlineData("1000000000", null)]
    [InlineData(null, "1000000")]
    public async Task ACapacityThatOverflowsTheColumnIs422InsteadOf500(string? weight, string? volume)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var body = new
        {
            parentId = (Guid?)null,
            code = "A",
            kind = "Zone",
            role = "Storage",
            allowsStorage = true,
            allowsPicking = false,
            maxWeightKg = weight is null ? (decimal?)null : decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture),
            maxVolumeM3 = volume is null ? (decimal?)null : decimal.Parse(volume, System.Globalization.CultureInfo.InvariantCulture),
            maxUnits = (int?)null,
            putawayPriority = 0,
            storageClasses = new[] { "SECO" },
        };

        var response = await client.SendAsync(Write(HttpMethod.Post, LocationsUrl(warehouseId), body), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task DeletingAZoneWithChildrenIs422HasChildren()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var zone = await PostLocationAsync(client, warehouseId, NewBody("A", "Zone", "Storage"));
        await PostLocationAsync(client, warehouseId, NewBody("A-01", "Bin", "Storage", zone.GetProperty("id").GetGuid()));

        var response = await client.SendAsync(
            Write(HttpMethod.Delete, $"{LocationsUrl(warehouseId)}/{zone.GetProperty("id").GetGuid()}"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.has_children", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheScannableListExcludesVirtualsAndCarriesTheEtag()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        var response = await ScannableAsync(client, warehouseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToList();
        Assert.Equal(["RECEIVING", "REC-01"], items);
        var etag = document.RootElement.GetProperty("etag").GetString();
        Assert.False(string.IsNullOrEmpty(etag));
        Assert.Equal(etag, response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task TheScannableEtagIsStableUntilALocationChanges()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);

        async Task<string?> EtagAsync() => (await ScannableAsync(client, warehouseId)).Headers.ETag?.ToString();

        var initial = await EtagAsync();
        Assert.Equal(initial, await EtagAsync());

        var bin = await PostLocationAsync(client, warehouseId, NewBody("B1", "Zone", "Storage"));
        var afterCreate = await EtagAsync();
        Assert.NotEqual(initial, afterCreate);

        var update = await client.SendAsync(
            Write(HttpMethod.Put, $"{LocationsUrl(warehouseId)}/{bin.GetProperty("id").GetGuid()}", PutBody(bin, "Blocked")), Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.NotEqual(afterCreate, await EtagAsync());
    }

    [Fact]
    public async Task IfNoneMatchWithTheCurrentEtagIs304()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        var etag = (await ScannableAsync(client, warehouseId)).Headers.ETag!.ToString();

        var exact = await ScannableAsync(client, warehouseId, etag);
        var weak = await ScannableAsync(client, warehouseId, "W/" + etag);
        var other = await ScannableAsync(client, warehouseId, "\"0000000000000000\"");

        Assert.Equal(HttpStatusCode.NotModified, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, weak.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsWarehouseIs403OnLocations()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var warehouseId = await CreateWarehouseAsync(client);
        await SeedTenantAsync(factory, OtherTenantId);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        var response = await outsider.GetAsync(LocationsUrl(warehouseId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }
}
