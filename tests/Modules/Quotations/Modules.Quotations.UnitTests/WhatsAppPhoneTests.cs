using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>`Customer.Phone` es texto libre: la regla local de 10 dígitos es la misma que Zenvia aplicaba desde siempre.</summary>
public sealed class WhatsAppPhoneTests
{
    [Theory]
    [InlineData("3001234567", "573001234567")]
    [InlineData("300 123 4567", "573001234567")]
    [InlineData("(300) 123-4567", "573001234567")]
    public void TenDigitsGetTheColombianPrefix(string phone, string expected) =>
        Assert.Equal(expected, WhatsAppPhone.Normalize(phone));

    [Theory]
    [InlineData("573001234567", "573001234567")]
    [InlineData("+57 300 123 4567", "573001234567")]
    [InlineData("14155550123", "14155550123")]
    public void LongerNumbersKeepTheirDigits(string phone, string expected) =>
        Assert.Equal(expected, WhatsAppPhone.Normalize(phone));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin teléfono")]
    public void BlankOrDigitlessIsNull(string? phone) =>
        Assert.Null(WhatsAppPhone.Normalize(phone));
}
