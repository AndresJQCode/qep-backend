using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Phones;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-09 §6.5: con «+» es internacional; si no, nacional del país; inválido → null.</summary>
public sealed class LibPhoneNumberNormalizerTests
{
    private readonly LibPhoneNumberNormalizer _normalizer = new();

    [Theory]
    [InlineData("+573001234567", "CO", "3001234567", "57")]
    [InlineData("+14155552671", "US", "4155552671", "1")]
    [InlineData("+34612345678", "ES", "612345678", "34")]
    [InlineData("+390612345678", "IT", "0612345678", "39")] // el cero italiano es parte del número nacional
    public void SplitsAValidNumberIntoRegionNationalDigitsAndCallingCode(string e164, string country, string national, string callingCode) =>
        Assert.Equal(new PhoneNumberParts(country, national, callingCode), _normalizer.Split(e164));

    [Theory]
    [InlineData("573001234567", "CO")]
    [InlineData("3001234567", "CO")]
    [InlineData("300 123 4567", "CO")]
    [InlineData("+57 300 123 4567", null)]
    [InlineData("+57 300 123 4567", "ES")] // con «+» la región se ignora
    public void ANumberWithoutPlusIsReadAsNationalOfTheDefaultRegion(string phone, string? region) =>
        Assert.Equal(new PhoneNumberParts("CO", "3001234567", "57"), _normalizer.Split(phone, region));

    [Fact]
    public void AVenezuelanMobileWithTheVenezuelanRegionGetsItsCallingCode() =>
        Assert.Equal(new PhoneNumberParts("VE", "4121234567", "58"), _normalizer.Split("4121234567", "VE"));

    [Theory]
    [InlineData("3001234567", null)]   // sin «+» y sin región: hoy
    [InlineData("3001234567", "")]
    [InlineData("573001234567", null)]
    [InlineData("584121234567", "CO")] // VE sin «+» leído como colombiano: inválido
    [InlineData("+57573001234567", "CO")] // prefijo doble
    [InlineData("3001234567", "XX")]   // región que la librería no conoce
    public void WhatCannotBeSplitIsNull(string phone, string? region) => Assert.Null(_normalizer.Split(phone, region));

    // Cliente desde un contacto compartido: el SPA recibe (país, nacional) de Split y los devuelve a POST /customers;
    // el teléfono del cliente (ToE164) tiene que ser el mismo E.164 original, o StartConversation escribe a otro número.
    [Theory]
    [InlineData("+573001234567")] // CO
    [InlineData("+14155552671")] // US
    [InlineData("+34612345678")] // ES
    [InlineData("+393123456789")] // IT, celular
    [InlineData("+390612345678")] // IT, fijo con el cero nacional
    [InlineData("+525512345678")] // MX
    [InlineData("+5491123456789")] // AR: el «9» del celular es parte del número nacional
    public void SplitPartsRoundTripToTheSameE164(string e164)
    {
        var parts = _normalizer.Split(e164);

        Assert.NotNull(parts);
        Assert.Equal(e164, _normalizer.ToE164(parts.NationalNumber, parts.Country));
    }

    [Theory]
    [InlineData("+3001234567")] // un wa_id sin indicativo: +30 (Grecia) no existe como ese número
    [InlineData("+57123")]
    [InlineData("+")]
    [InlineData("+abc")]
    [InlineData("")]
    [InlineData(null)]
    public void AnInvalidNumberIsNotSplit(string? e164) => Assert.Null(_normalizer.Split(e164));

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
