using Modules.Customers.Infrastructure.Phones;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-09 §6.5: con «+» es internacional; si no, nacional del país; inválido → null.</summary>
public sealed class LibPhoneNumberNormalizerTests
{
    private readonly LibPhoneNumberNormalizer _normalizer = new();

    // La larga distancia colombiana es «0» + el dígito del operador + el número completo («03 300…»):
    // la librería la quita. «0300…» con el 0 pegado al celular no es un formato real, y da null.
    [Theory]
    [InlineData("+57 300 123 4567", "ES", "+573001234567")]
    [InlineData("300 123 4567", "CO", "+573001234567")]
    [InlineData("(310) 935-2187", "CO", "+573109352187")]
    [InlineData("03 300 123 4567", "CO", "+573001234567")]
    [InlineData("033001234567", "CO", "+573001234567")]
    [InlineData("612 34 56 78", "ES", "+34612345678")]
    [InlineData("+1 (415) 555-2671", "CO", "+14155552671")]
    public void ValidNumbersBecomeE164WithPlus(string phone, string country, string expected) =>
        Assert.Equal(expected, _normalizer.ToE164(phone, country));

    [Theory]
    [InlineData("", "CO")]
    [InlineData("   ", "CO")]
    [InlineData("abc", "CO")]
    [InlineData("12", "CO")]
    [InlineData("300 123 4567", "ZZ")]
    [InlineData(null, "CO")]
    public void InvalidNumbersAreNullNeverAnException(string? phone, string country) =>
        Assert.Null(_normalizer.ToE164(phone, country));

    // Spec 2026-10-10 §6.2: el país del incompleto sale del prefijo del BSUID si libphonenumber lo conoce.
    [Theory]
    [InlineData("CO", true)]
    [InlineData("us", true)]
    [InlineData("XX", false)]
    [InlineData("", false)]
    public void KnownRegionsAreTheOnesLibPhoneNumberSupports(string region, bool expected) =>
        Assert.Equal(expected, new LibPhoneNumberNormalizer().IsKnownRegion(region));

    [Theory]
    [InlineData("+573001234567", "CO")]
    [InlineData("+14155550100", "US")]
    [InlineData("3001234567", null)]
    [InlineData(null, null)]
    public void TheRegionOfAnE164NumberIsItsCountry(string? e164, string? expected) =>
        Assert.Equal(expected, new LibPhoneNumberNormalizer().RegionOf(e164));
}
