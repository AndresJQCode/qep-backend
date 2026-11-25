using Modules.Customers.Application;

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
