using Modules.Wms.Application;

namespace Modules.Wms.UnitTests;

public sealed class UpdateVariantProfileValidatorTests
{
    private static readonly UpdateVariantProfileCommand Valid = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "UND",
        0,
        "None",
        false,
        1.5m,
        0.01m,
        10m,
        10m,
        10m,
        "FRIO",
        null,
        [new UomConversionRequest("CAJA x12", 12m, "7701234567890")],
        0);

    private static FluentValidation.Results.ValidationResult Validate(UpdateVariantProfileCommand command) =>
        new UpdateVariantProfileValidator().Validate(command);

    private static void AssertInvalid(UpdateVariantProfileCommand command, string propertyName) =>
        Assert.Contains(Validate(command).Errors, error => error.PropertyName == propertyName);

    [Fact]
    public void AValidCommandPasses() => Assert.True(Validate(Valid).IsValid);

    [Theory]
    [InlineData("Banana")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("lot")]
    public void ATrackingOutsideTheEnumIsInvalid(string tracking) => AssertInvalid(Valid with { Tracking = tracking }, "Tracking");

    [Theory]
    [InlineData("None")]
    [InlineData("Lot")]
    [InlineData("Serial")]
    public void EveryTrackingNameIsValid(string tracking) => Assert.True(Validate(Valid with { Tracking = tracking }).IsValid);

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void APrecisionOutsideZeroToSixIsInvalid(int precision) =>
        AssertInvalid(Valid with { BaseUomPrecision = precision }, "BaseUomPrecision");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("NOVECARACT")]
    public void AnEmptyOrOverlongBaseUomIsInvalid(string baseUom) => AssertInvalid(Valid with { BaseUom = baseUom }, "BaseUom");

    [Fact]
    public void AnOverlongDisplayUomIsInvalid() => AssertInvalid(Valid with { DisplayUom = "NOVECARACT" }, "DisplayUom");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AConversionWithAnEmptyCodeIsInvalidWithTheIndexedName(string code) =>
        AssertInvalid(Valid with { Conversions = [new UomConversionRequest(code, 12m, null)] }, "Conversions[0].Code");

    [Fact]
    public void AConversionCodeLongerThan32IsInvalid() =>
        AssertInvalid(Valid with { Conversions = [new UomConversionRequest(new string('X', 33), 12m, null)] }, "Conversions[0].Code");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AConversionFactorThatIsNotPositiveIsInvalid(int factor) =>
        AssertInvalid(
            Valid with { Conversions = [new UomConversionRequest("A", 1m, null), new UomConversionRequest("B", factor, null)] },
            "Conversions[1].Factor");

    [Fact]
    public void AConversionBarcodeLongerThan64IsInvalid() =>
        AssertInvalid(Valid with { Conversions = [new UomConversionRequest("A", 1m, new string('9', 65))] }, "Conversions[0].Barcode");

    [Fact]
    public void MoreThanFiftyConversionsAreInvalid()
    {
        var many = Enumerable.Range(1, 51).Select(n => new UomConversionRequest($"P{n}", n, null)).ToList();

        AssertInvalid(Valid with { Conversions = many }, "Conversions");
    }

    [Fact]
    public void ExactlyFiftyConversionsPass()
    {
        var fifty = Enumerable.Range(1, 50).Select(n => new UomConversionRequest($"P{n}", n, null)).ToList();

        Assert.True(Validate(Valid with { Conversions = fifty }).IsValid);
    }

    [Fact]
    public void AStorageClassLongerThan32IsInvalid() => AssertInvalid(Valid with { StorageClass = new string('F', 33) }, "StorageClass");

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1_000_000_000)]
    public void AWeightOutsideTheColumnRangeIsInvalidInsteadOfOverflowingTheDatabase(double weight) =>
        AssertInvalid(Valid with { WeightKg = (decimal)weight }, "WeightKg");

    [Theory]
    [InlineData("0.0000001")]
    [InlineData("0.0000004")]
    [InlineData("12.1234567")]
    public void AFactorFinerThanTheColumnScaleIsInvalidInsteadOfTrippingTheCheck(string factor) =>
        AssertInvalid(
            Valid with { Conversions = [new UomConversionRequest("A", decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture), null)] },
            "Conversions[0].Factor");

    [Theory]
    [InlineData("0.000001")]
    [InlineData("12.123456")]
    public void AFactorWithUpToSixDecimalsPasses(string factor) =>
        Assert.True(Validate(
            Valid with { Conversions = [new UomConversionRequest("A", decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture), null)] }).IsValid);
}
