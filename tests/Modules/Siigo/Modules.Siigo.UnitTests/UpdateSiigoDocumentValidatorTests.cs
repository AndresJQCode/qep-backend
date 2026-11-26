using Modules.Siigo.Application.Settings;

namespace Modules.Siigo.UnitTests;

/// <summary>El <c>priceMode</c> del <c>PUT …/document</c>: ausente conserva el guardado; presente debe ser un nombre del enum, sin
/// distinguir mayúsculas.</summary>
public sealed class UpdateSiigoDocumentValidatorTests
{
    private static UpdateSiigoDocumentCommand Command(string? priceMode) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "doc-1", null, null, false, false, false, null, priceMode, 0);

    [Theory]
    [InlineData(null)]
    [InlineData("BaseWithDiscount")]
    [InlineData("DiscountedPrice")]
    [InlineData("discountedprice")]
    public void AKnownOrMissingPriceModeIsValid(string? priceMode)
    {
        var result = new UpdateSiigoDocumentValidator().Validate(Command(priceMode));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("BaseWithDiscount,DiscountedPrice")]
    [InlineData("+1")]
    public void AnUnknownPriceModeFailsOnThePriceModeField(string priceMode)
    {
        var result = new UpdateSiigoDocumentValidator().Validate(Command(priceMode));

        Assert.Contains(result.Errors, error => error.PropertyName == "priceMode");
    }
}
