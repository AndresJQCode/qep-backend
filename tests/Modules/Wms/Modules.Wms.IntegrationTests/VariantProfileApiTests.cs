using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Perfiles logísticos de variante (RF-006, RF-007, RF-009, RF-010, ADR-07, ADR-08, P-8) de punta a punta por HTTP.</summary>
public sealed class VariantProfileApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProfilesUrl => $"{WmsUrl()}/variant-profiles";

    private sealed class VariantWithStockProbe : IVariantStockProbe
    {
        public Task<bool> HasStockAsync(Guid tenantId, Guid variantId, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlySet<Guid>> HasStockForManyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(variantIds.ToHashSet());
    }

    private static object Body(
        long version,
        string baseUom = "UND",
        int baseUomPrecision = 0,
        string tracking = "None",
        object[]? conversions = null,
        string? displayUom = null) =>
        new
        {
            baseUom,
            baseUomPrecision,
            tracking,
            tracksExpiry = false,
            weightKg = 1.5m,
            volumeM3 = (decimal?)null,
            lengthCm = (decimal?)null,
            widthCm = (decimal?)null,
            heightCm = (decimal?)null,
            storageClass = "FRIO",
            displayUom,
            conversions = conversions ?? [],
            version,
        };

    private static HttpRequestMessage Put(Guid variantId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{ProfilesUrl}/{variantId}") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    private static async Task<JsonElement> GetOneAsync(HttpClient client, Guid variantId)
    {
        var response = await client.GetAsync($"{ProfilesUrl}?variantIds={variantId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await BodyAsync(response)).RootElement.GetProperty("items");
        return items[0].Clone();
    }

    private static async Task<List<(string Kind, string Symbology, string Value, bool IsPrimary, Guid? ConversionId)>> IdentifiersAsync(
        QepApiFactory factory, Guid variantId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT kind, symbology, value, is_primary, conversion_id FROM wms.identifiers WHERE target_id = @id ORDER BY value",
            connection);
        command.Parameters.AddWithValue("id", variantId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string, string, bool, Guid?)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }

        return rows;
    }

    [Fact]
    public async Task GettingAnUnsavedProfileReturnsTheDefaultWithConversionsFromPackagingUnits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", "7701234567890", [12, 144], "Camisa");
        using var client = CreateManager(factory);

        var profile = await GetOneAsync(client, variantId);

        Assert.Equal(variantId, profile.GetProperty("variantId").GetGuid());
        Assert.Equal("Camisa", profile.GetProperty("productName").GetString());
        Assert.Equal("SKU-1", profile.GetProperty("variantCode").GetString());
        Assert.Equal("7701234567890", profile.GetProperty("barcode").GetString());
        Assert.Equal("UND", profile.GetProperty("baseUom").GetString());
        Assert.Equal(0, profile.GetProperty("baseUomPrecision").GetInt32());
        Assert.Equal("None", profile.GetProperty("tracking").GetString());
        Assert.Equal(0, profile.GetProperty("version").GetInt64());
        Assert.False(profile.GetProperty("hasStock").GetBoolean());
        var conversions = profile.GetProperty("conversions").EnumerateArray()
            .Select(c => (c.GetProperty("code").GetString(), c.GetProperty("factor").GetDecimal(), c.GetProperty("id").GetGuid()))
            .OrderBy(c => c.Item2)
            .ToList();
        Assert.Equal([("EMPAQUE x12", 12m, Guid.Empty), ("EMPAQUE x144", 144m, Guid.Empty)], conversions);
    }

    [Fact]
    public async Task UnknownVariantIdsAreOmitted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, [12]);
        using var client = CreateManager(factory);

        var response = await client.GetAsync($"{ProfilesUrl}?variantIds={Guid.NewGuid()},{variantId},{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await BodyAsync(response)).RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(variantId, items[0].GetProperty("variantId").GetGuid());
    }

    [Fact]
    public async Task MoreThanTwoHundredIdsIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var ids = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()));

        var response = await client.GetAsync($"{ProfilesUrl}?variantIds={ids}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task NoIdsAtAllIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.GetAsync(ProfilesUrl, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AMalformedIdIs422InsteadOfAServerError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.GetAsync($"{ProfilesUrl}?variantIds=not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TheFirstPutCreatesTheProfileTheIdentifiersAndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", "7701234567890", [12, 144]);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Put(variantId, Body(0, conversions: [new { code = "CAJA x12", factor = 12m, barcode = "7700000000012" }])), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.Equal(1.5m, root.GetProperty("weightKg").GetDecimal());
        Assert.Equal("FRIO", root.GetProperty("storageClass").GetString());
        var conversion = Assert.Single(root.GetProperty("conversions").EnumerateArray());
        Assert.NotEqual(Guid.Empty, conversion.GetProperty("id").GetGuid());

        var identifiers = await IdentifiersAsync(factory, variantId);
        Assert.Contains(identifiers, i => i is { Kind: "Variant", Symbology: "Internal", Value: "SKU-1", IsPrimary: false });
        Assert.Contains(identifiers, i => i is { Kind: "Variant", Symbology: "Gtin", Value: "7701234567890", IsPrimary: true });
        Assert.Contains(
            identifiers,
            i => i.Kind == "Presentation" && i.Symbology == "Gtin" && i.Value == "7700000000012" && i.ConversionId == conversion.GetProperty("id").GetGuid());
        Assert.Equal(3, identifiers.Count);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.profile.created", variantId.ToString()));

        // El GET ya no ve el default sino lo guardado.
        var stored = await GetOneAsync(client, variantId);
        Assert.Equal(1, stored.GetProperty("version").GetInt64());
        Assert.Equal("CAJA x12", Assert.Single(stored.GetProperty("conversions").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASecondPutUpdatesKeepsTheConversionIdAndReplacesThePresentationIdentifiers()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        var created = await client.SendAsync(
            Put(variantId, Body(0, conversions: [new { code = "CAJA", factor = 12m, barcode = "BAR-A" }, new { code = "PACK", factor = 6m, barcode = "BAR-B" }])),
            Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var firstId = JsonDocument.Parse(await BodyAsync(created)).RootElement.GetProperty("conversions").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "CAJA").GetProperty("id").GetGuid();

        var response = await client.SendAsync(
            Put(variantId, Body(1, conversions: [new { code = "CAJA", factor = 24m, barcode = "BAR-C" }])), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
        var conversion = Assert.Single(document.RootElement.GetProperty("conversions").EnumerateArray());
        Assert.Equal(firstId, conversion.GetProperty("id").GetGuid());
        Assert.Equal(24m, conversion.GetProperty("factor").GetDecimal());
        var presentations = (await IdentifiersAsync(factory, variantId)).Where(i => i.Kind == "Presentation").ToList();
        var only = Assert.Single(presentations);
        Assert.Equal("BAR-C", only.Value);
        Assert.Equal(firstId, only.ConversionId);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.profile.updated", variantId.ToString()));
    }

    [Fact]
    public async Task AVariantWhoseCodeEqualsItsBarcodeCanSaveItsProfileTwice()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        // SKU = EAN es válido en Catalog (los chequea en conjuntos separados) y es común en retail.
        var variantId = await CreateVariantAsync(factory, "7701234567890", "7701234567890", []);
        using var client = CreateManager(factory);

        var first = await client.SendAsync(Put(variantId, Body(0)), Ct);
        var second = await client.SendAsync(Put(variantId, Body(1)), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var identifier = Assert.Single(await IdentifiersAsync(factory, variantId));
        Assert.Equal(("Variant", "Gtin", "7701234567890", true), (identifier.Kind, identifier.Symbology, identifier.Value, identifier.IsPrimary));
    }

    [Fact]
    public async Task AFactorFinerThanTheColumnScaleIs422InsteadOf500()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Put(variantId, Body(0, conversions: [new { code = "CAJA", factor = 0.0000001m, barcode = (string?)null }])), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ChangingTrackingWithoutStockIsAllowed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(variantId, Body(0)), Ct)).StatusCode);

        var response = await client.SendAsync(Put(variantId, Body(1, tracking: "Lot")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("Lot", document.RootElement.GetProperty("tracking").GetString());
    }

    [Fact]
    public async Task ChangingTrackingWithStockIs422()
    {
        var (database, factory) = await OpenAsync(configureServices: services => services.AddScoped<IVariantStockProbe, VariantWithStockProbe>());
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(variantId, Body(0)), Ct)).StatusCode);

        var response = await client.SendAsync(Put(variantId, Body(1, tracking: "Lot")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.profile.tracking_change_requires_identification", await CodeOfAsync(response));
        var stored = await GetOneAsync(client, variantId);
        Assert.Equal("None", stored.GetProperty("tracking").GetString());
        Assert.True(stored.GetProperty("hasStock").GetBoolean());
    }

    // I4 (pendiente de confirmación del owner): con saldo la unidad base no cambia; sin saldo, sí.
    [Fact]
    public async Task WithStockChangingTheBaseUomIs422()
    {
        var (database, factory) = await OpenAsync(configureServices: services => services.AddScoped<IVariantStockProbe, VariantWithStockProbe>());
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(variantId, Body(0)), Ct)).StatusCode);

        var response = await client.SendAsync(Put(variantId, Body(1, baseUom: "KG")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.profile.base_uom_change_requires_identification", await CodeOfAsync(response));
        Assert.Equal("UND", (await GetOneAsync(client, variantId)).GetProperty("baseUom").GetString());
    }

    [Fact]
    public async Task WithoutStockChangingTheBaseUomIs200()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(variantId, Body(0)), Ct)).StatusCode);

        var response = await client.SendAsync(Put(variantId, Body(1, baseUom: "KG")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("KG", document.RootElement.GetProperty("baseUom").GetString());
    }

    [Fact]
    public async Task AnUnknownBaseUomIs422UomUnknown()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(variantId, Body(0, baseUom: "ZZZ")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.uom.unknown", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnUnknownDisplayUomIs422UomUnknown()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(variantId, Body(0, displayUom: "ZZZ")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.uom.unknown", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PrecisionAboveTheUnitIs422PrecisionExceeded()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(variantId, Body(0, baseUom: "UND", baseUomPrecision: 2)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.uom.precision_exceeded", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AConversionBarcodeTakenByAnotherVariantIs422ValueTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var first = await CreateVariantAsync(factory, "SKU-1", "7701234567890", [], "Uno");
        var second = await CreateVariantAsync(factory, "SKU-2", null, [], "Dos");
        using var client = CreateManager(factory);
        // El código de barras de la primera se copia como identificador al guardar su perfil.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(first, Body(0)), Ct)).StatusCode);

        var response = await client.SendAsync(
            Put(second, Body(0, conversions: [new { code = "CAJA", factor = 12m, barcode = "7701234567890" }])), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.identifier.value_taken", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(variantId, Body(0)), Ct)).StatusCode);

        // Con la fila ya creada, volver a mandar la versión 0 es mandar una lectura vieja.
        var again = await client.SendAsync(Put(variantId, Body(0)), Ct);
        var wrong = await client.SendAsync(Put(variantId, Body(9)), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, again.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, wrong.StatusCode);
    }

    [Fact]
    public async Task AVersionAboveZeroWithoutARowIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(variantId, Body(3)), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task AVariantOfAnotherTenantIs422VariantUnknown()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        var foreign = await CreateVariantAsync(factory, "SKU-X", null, [], tenantId: OtherTenantId);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(foreign, Body(0)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.profile.variant_unknown", await CodeOfAsync(response));
        var listed = await client.GetAsync($"{ProfilesUrl}?variantIds={foreign}", Ct);
        Assert.Equal(0, JsonDocument.Parse(await BodyAsync(listed)).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnEmptyConversionCodeIs422ValidationFailedWithTheIndexedField()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Put(variantId, Body(0, conversions: [new { code = "", factor = 12m, barcode = (string?)null }])), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Conversions[0].Code", out var codeErrors));
        Assert.NotEqual(JsonValueKind.Null, codeErrors.ValueKind);
    }

    [Fact]
    public async Task ADuplicateConversionCodeIs422ConversionDuplicate()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Put(variantId, Body(0, conversions: [new { code = "Caja", factor = 12m, barcode = (string?)null }, new { code = "CAJA", factor = 6m, barcode = (string?)null }])),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.profile.conversion_duplicate", await CodeOfAsync(response));
    }

    private static async Task<Dictionary<string, string?>> BarcodesByCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("conversions").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("barcode").GetString());
    }

    [Fact]
    public async Task MovingABarcodeBetweenConversionsInOnePutWorks()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(
                Put(variantId, Body(0, conversions: [new { code = "A", factor = 2m, barcode = "BAR-X" }, new { code = "B", factor = 4m, barcode = (string?)null }])),
                Ct)).StatusCode);

        var response = await client.SendAsync(
            Put(variantId, Body(1, conversions: [new { code = "A", factor = 2m, barcode = (string?)null }, new { code = "B", factor = 4m, barcode = "BAR-X" }])),
            Ct);

        var barcodes = await BarcodesByCodeAsync(response);
        Assert.Null(barcodes["A"]);
        Assert.Equal("BAR-X", barcodes["B"]);
        var presentations = (await IdentifiersAsync(factory, variantId)).Where(i => i.Kind == "Presentation").ToList();
        Assert.Equal("BAR-X", Assert.Single(presentations).Value);
    }

    [Fact]
    public async Task SwappingTwoBarcodesInOnePutWorks()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", null, []);
        using var client = CreateManager(factory);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(
                Put(variantId, Body(0, conversions: [new { code = "A", factor = 2m, barcode = "BAR-X" }, new { code = "B", factor = 4m, barcode = "BAR-Y" }])),
                Ct)).StatusCode);

        var response = await client.SendAsync(
            Put(variantId, Body(1, conversions: [new { code = "A", factor = 2m, barcode = "BAR-Y" }, new { code = "B", factor = 4m, barcode = "BAR-X" }])),
            Ct);

        var barcodes = await BarcodesByCodeAsync(response);
        Assert.Equal("BAR-Y", barcodes["A"]);
        Assert.Equal("BAR-X", barcodes["B"]);
        var presentations = (await IdentifiersAsync(factory, variantId)).Where(i => i.Kind == "Presentation").ToList();
        Assert.Equal(["BAR-X", "BAR-Y"], presentations.Select(i => i.Value).Order().ToArray());
    }
}
