using Modules.Customers.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>El adaptador de <c>IBillingPhoneNormalizer</c> sólo traduce la firma: el país ausente es CO y el resto lo decide Customers.</summary>
public sealed class BillingPhoneNormalizerTests
{
    [Fact]
    public void ADigitsOnlyNumberWithoutCountryIsNationalColombian()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new BillingPhoneNormalizer(inner);

        var result = normalizer.ToE164("3001234567", null);

        Assert.Equal("+57-3001234567", result);
        Assert.Equal("CO", inner.LastCountry);
    }

    [Fact]
    public void ABlankCountryFallsBackToColombia()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new BillingPhoneNormalizer(inner);

        normalizer.ToE164("3001234567", "  ");

        Assert.Equal("CO", inner.LastCountry);
    }

    [Fact]
    public void AnExplicitCountryIsPassedThroughAndNullStaysNull()
    {
        var inner = new RecordingNormalizer();
        var normalizer = new BillingPhoneNormalizer(inner);

        Assert.Equal("+52-5512345678", normalizer.ToE164("5512345678", "MX"));
        Assert.Equal("MX", inner.LastCountry);
        Assert.Null(normalizer.ToE164(null, "MX"));
    }

    private sealed class RecordingNormalizer : IPhoneNumberNormalizer
    {
        public string? LastCountry { get; private set; }

        public string? ToE164(string? phone, string country)
        {
            LastCountry = country;
            return phone is null ? null : $"+{(country == "CO" ? "57" : "52")}-{phone}";
        }

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }
}
