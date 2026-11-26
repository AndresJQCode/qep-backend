using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Phones;

namespace Bootstrapper.UnitTests;

/// <summary>El adaptador del partidor de teléfonos del Excel de pedidos (spec 2026-11-25 indicativo) sobre
/// <see cref="IPhoneNumberSplitter"/> de Customers: sin región de respaldo, así que sólo parte lo que trae su
/// país, y devuelve el número nacional.</summary>
public sealed class QuotationPhoneNumbersTests
{
    [Fact]
    public void ReturnsTheNationalNumberAndAsksWithoutADefaultRegion()
    {
        var splitter = new RecordingSplitter(new PhoneNumberParts("ES", "612345678", "34"));

        var national = new QuotationPhoneNumbers(splitter).NationalNumberOf("+34612345678");

        Assert.Equal("612345678", national);
        Assert.Equal(("+34612345678", (string?)null), Assert.Single(splitter.Calls));
    }

    [Fact]
    public void ReturnsNullWhenThePhoneCannotBeSplit()
    {
        var national = new QuotationPhoneNumbers(new RecordingSplitter(null)).NationalNumberOf("+99912");

        Assert.Null(national);
    }

    // Contra el normalizador real de Customers: lo que se verifica depende de cómo parte libphonenumber.
    [Theory]
    [InlineData("+573001234567", "3001234567")]
    [InlineData("+34612345678", "612345678")]
    [InlineData("+390612345678", "0612345678")] // en Italia el cero inicial es parte del número
    [InlineData("+576015550000 ext 123", null)] // el partidor descarta la extensión: se perderían dígitos
    [InlineData("+80012345678", null)] // no geográfico (región 001): sin el indicativo no se puede marcar
    public void NeverLosesDigitsWithTheRealNormalizer(string phone, string? expected)
    {
        var national = new QuotationPhoneNumbers(new LibPhoneNumberNormalizer()).NationalNumberOf(phone);

        Assert.Equal(expected, national);
    }

    private sealed class RecordingSplitter(PhoneNumberParts? parts) : IPhoneNumberSplitter
    {
        public List<(string? Phone, string? Region)> Calls { get; } = [];

        public PhoneNumberParts? Split(string? phone, string? defaultRegion = null)
        {
            Calls.Add((phone, defaultRegion));
            return parts;
        }
    }
}
