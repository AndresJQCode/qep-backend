using Modules.Customers.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>El adaptador de <c>IServiceContractsPhoneNormalizer</c> sólo traduce la firma: el país ausente es CO y el resto lo decide Customers.</summary>
public sealed class ServiceContractsPhoneNormalizerTests
{
    [Fact]
    public void ADigitsOnlyNumberWithoutCountryIsNationalColombian()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new ServiceContractsPhoneNormalizer(inner);

        var result = normalizer.ToE164("3001234567", null);

        Assert.Equal("+57-3001234567", result);
        Assert.Equal("CO", inner.LastCountry);
    }

    [Fact]
    public void ABlankCountryFallsBackToColombia()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new ServiceContractsPhoneNormalizer(inner);

        normalizer.ToE164("3001234567", "  ");

        Assert.Equal("CO", inner.LastCountry);
    }

    [Fact]
    public void AnExplicitCountryIsPassedThroughAndNullStaysNull()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new ServiceContractsPhoneNormalizer(inner);

        Assert.Equal("+52-5512345678", normalizer.ToE164("5512345678", "MX"));
        Assert.Equal("MX", inner.LastCountry);
        Assert.Null(normalizer.ToE164(null, "MX"));
    }

    [Fact]
    public void AKnownCountryIsAskedInUpperCase()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new ServiceContractsPhoneNormalizer(inner);

        Assert.True(normalizer.IsKnownCountry(" mx "));
        Assert.Equal("MX", inner.LastRegion);
        Assert.False(normalizer.IsKnownCountry("ZZ"));
    }

    private sealed class RecordingNormalizer : IPhoneNumberNormalizer
    {
        public string? LastCountry { get; private set; }

        public string? LastRegion { get; private set; }

        public string? ToE164(string? phone, string country)
        {
            LastCountry = country;
            return phone is null ? null : $"+{(country == "CO" ? "57" : "52")}-{phone}";
        }

        public bool IsKnownRegion(string regionCode)
        {
            LastRegion = regionCode;
            return regionCode != "ZZ";
        }

        public string? RegionOf(string? e164) => null;
    }
}
