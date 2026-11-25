using System.Globalization;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>La lista de revisiones, la tasa de IPC y la confirmación en lote (spec reajuste §8): los campos que se rechazan.</summary>
public sealed class PriceReviewValidatorTests
{
    // 02:00 UTC del 1 de enero de 2028 es todavía 2027 en Bogotá: el último año de IPC es 2028, no 2029.
    private static readonly FixedTenantClock Clock = new(new DateTimeOffset(2028, 1, 1, 2, 0, 0, TimeSpan.Zero));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string[]> RateErrorsAsync(int year, string body) =>
        (await new SaveIpcRateValidator(Clock).ValidateAsync(
            SaveIpcRateCommand.From(Guid.NewGuid(), year, JsonSerializer.Deserialize<SaveIpcRateRequest>(body, Web)!), Ct))
        .Errors.Select(error => error.PropertyName).Distinct().ToArray();

    private static async Task<string[]> ConfirmErrorsAsync(int? year, IReadOnlyList<ConfirmIpcItemInput>? items) =>
        (await new ConfirmIpcReviewsValidator().ValidateAsync(
            ConfirmIpcReviewsCommand.From(Guid.NewGuid(), new ConfirmIpcRequest(year, items)), Ct))
        .Errors.Select(error => error.PropertyName).Distinct().ToArray();

    private static ConfirmIpcItemInput Item(
        Guid? serviceId = null, decimal? expectedPrice = 100m, decimal? newPrice = null, string? reason = null) =>
        new(serviceId ?? Guid.NewGuid(), 1, expectedPrice, newPrice, reason);

    [Theory]
    [InlineData("all", 25, 1, "")]
    [InlineData("DUE", 100, 1, "")]
    [InlineData("Held", 1, 3, "")]
    [InlineData("scheduled", 25, 1, "State")]
    [InlineData("", 25, 1, "State")]
    [InlineData("all", 101, 1, "PageSize")]
    [InlineData("all", 0, 1, "PageSize")]
    [InlineData("all", 25, 0, "Page")]
    public async Task TheReviewListRejectsAnUnknownStateAndAPageOutOfRange(string state, int pageSize, int page, string field)
    {
        var result = await new ListPriceReviewsValidator().ValidateAsync(
            new ListPriceReviewsQuery(Guid.NewGuid(), state, false, null, page, pageSize), Ct);

        Assert.Equal(field.Length == 0 ? [] : [field], result.Errors.Select(error => error.PropertyName).Distinct());
    }

    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    [InlineData(2028, false)]
    [InlineData(2029, true)]
    public async Task TheIpcYearGoesFrom2000ToTheYearAfterTheTenantToday(int year, bool rejected) =>
        Assert.Equal(rejected, (await RateErrorsAsync(year, """{"rate":5.2,"expectedRate":null}""")).Contains("Year"));

    [Theory]
    [InlineData("5.2", false)]
    [InlineData("-10", false)]
    [InlineData("100", false)]
    [InlineData("100.01", true)]
    [InlineData("-10.01", true)]
    [InlineData("5.123", true)]
    [InlineData("null", true)]
    public async Task TheIpcRateIsRequiredAndBounded(string rate, bool rejected) =>
        Assert.Equal(rejected, (await RateErrorsAsync(2027, $$"""{"rate":{{rate}},"expectedRate":null}""")).Contains("Rate"));

    // Ausente no es null: null dice «la pantalla no mostraba ninguna», y ausente no dice nada.
    [Theory]
    [InlineData("""{"rate":5.2}""", true)]
    [InlineData("""{"rate":5.2,"expectedRate":null}""", false)]
    [InlineData("""{"rate":5.2,"expectedRate":4.85}""", false)]
    [InlineData("""{"rate":5.2,"expectedRate":4.855}""", true)]
    [InlineData("""{"rate":5.2,"expectedRate":"4.85"}""", true)]
    [InlineData("""{"rate":5.2,"expectedRate":{}}""", true)]
    [InlineData("""{"rate":5.2,"expectedRate":1e400}""", true)]
    public async Task TheExpectedRateTravelsAlwaysAsANumberOrNull(string body, bool rejected) =>
        Assert.Equal(rejected, (await RateErrorsAsync(2027, body)).Contains("ExpectedRate"));

    // /openapi/v1.json arma el esquema de cada cuerpo con JsonSchemaExporter: un parámetro `JsonElement X = default` en el
    // constructor del record tumbaba el documento entero (precedente UpdateTaxRateRequest en Catalog).
    [Fact]
    public void TheIpcRateRequestSchemaCanBeExported() =>
        Assert.NotNull(Web.GetJsonSchemaAsNode(typeof(SaveIpcRateRequest))["properties"]?["expectedRate"]);

    [Fact]
    public async Task TheBatchTakesFromOneToTwoHundredContractsWithoutRepeating()
    {
        var repeated = Guid.NewGuid();

        Assert.Equal(["Items"], await ConfirmErrorsAsync(2027, []));
        Assert.Equal(["Items"], await ConfirmErrorsAsync(2027, null));
        Assert.Empty(await ConfirmErrorsAsync(2027, Enumerable.Range(0, 200).Select(_ => Item()).ToList()));
        Assert.Equal(["Items"], await ConfirmErrorsAsync(2027, Enumerable.Range(0, 201).Select(_ => Item()).ToList()));
        Assert.Equal(["Items"], await ConfirmErrorsAsync(2027, [Item(repeated), Item(repeated)]));
        Assert.Equal(["Year"], await ConfirmErrorsAsync(null, [Item()]));
    }

    [Theory]
    [InlineData(null, null, 0, "Items[1].ExpectedPrice")]
    [InlineData("100", "0", 0, "Items[1].NewPrice")]
    [InlineData("100", "-5", 0, "Items[1].NewPrice")]
    [InlineData("100", "10.123", 0, "Items[1].NewPrice")]
    [InlineData("100", "10.12", 501, "Items[1].Reason")]
    [InlineData("100", "10.12", 500, "")]
    public async Task EachItemNamesItsOwnField(string? expected, string? newPrice, int reasonLength, string field)
    {
        decimal? Number(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);
        var item = Item(expectedPrice: Number(expected), newPrice: Number(newPrice), reason: new string('x', reasonLength));

        var errors = await ConfirmErrorsAsync(2027, [Item(), item]);

        Assert.Equal(field.Length == 0 ? [] : [field], errors);
    }

    [Fact]
    public async Task AnItemNeedsItsServiceAndVersion()
    {
        var errors = await ConfirmErrorsAsync(2027, [new ConfirmIpcItemInput(null, null, 100m, null, null)]);

        Assert.Equal(["Items[0].ServiceId", "Items[0].Version"], errors.Order());
        Assert.Equal(["Items[0]"], await ConfirmErrorsAsync(2027, [null!]));
    }
}
