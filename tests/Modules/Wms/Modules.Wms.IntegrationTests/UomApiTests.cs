using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Catálogo de unidades de medida del tenant (RF-008, §16.1) de punta a punta por HTTP.</summary>
public sealed class UomApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UomsUrl => $"{WmsUrl()}/uoms";

    private static HttpRequestMessage Post(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, UomsUrl) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    [Fact]
    public async Task ListingANewTenantReturnsTheSixSystemUnitsFirst()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.GetAsync(UomsUrl, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var units = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                item.GetProperty("code").GetString(),
                item.GetProperty("precision").GetInt32(),
                item.GetProperty("isSystem").GetBoolean()))
            .ToList();
        Assert.Equal(6, units.Count);
        Assert.All(units, unit => Assert.True(unit.Item3));
        Assert.Equal(
            new[] { ("G", 0), ("KG", 3), ("L", 3), ("M", 2), ("ML", 0), ("UND", 0) },
            units.Select(unit => (unit.Item1!, unit.Item2)).OrderBy(unit => unit.Item1, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CreatingACustomUnitReturns201LowercaseNormalizedAndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Post(new { code = "caja", name = "Caja", precision = 0 }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("CAJA", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Caja", document.RootElement.GetProperty("name").GetString());
        Assert.False(document.RootElement.GetProperty("isSystem").GetBoolean());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.uom.created", "CAJA"));

        var listed = await client.GetAsync(UomsUrl, Ct);
        using var list = JsonDocument.Parse(await BodyAsync(listed));
        var items = list.RootElement.GetProperty("items");
        Assert.Equal(7, items.GetArrayLength());
        Assert.Equal("CAJA", items[6].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ADuplicateCodeIs422CodeTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Post(new { code = "UND", name = "Otra unidad", precision = 0 }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.uom.code_taken", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PrecisionSevenIs422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Post(new { code = "CAJA", name = "Caja", precision = 7 }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Precision", out var precisionErrors));
        Assert.NotEqual(JsonValueKind.Null, precisionErrors.ValueKind);
    }

    [Theory]
    [InlineData("", "Caja", "Code")]
    [InlineData("TOOLONGCODE", "Caja", "Code")]
    [InlineData("CAJA", "", "Name")]
    [InlineData("CAJA", "0123456789012345678901234567890123456789X", "Name")]
    public async Task ABadCodeOrNameIs422ValidationFailedWithTheField(string code, string name, string field)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Post(new { code, name, precision = 0 }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out var fieldErrors));
        Assert.NotEqual(JsonValueKind.Null, fieldErrors.ValueKind);
    }

    [Fact]
    public async Task ACodeWithSymbolsIs422CodeInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Post(new { code = "CA-JA", name = "Caja", precision = 0 }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.uom.code_invalid", await CodeOfAsync(response));
    }
}
