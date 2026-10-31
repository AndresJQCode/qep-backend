using Modules.Customers.Infrastructure.Phones;

namespace Bootstrapper.UnitTests;

/// <summary>Decisión del owner 2026-10-30: el número externo se valida de verdad, contra el normalizador real de Customers.</summary>
public sealed class DialablePhoneNumbersTests
{
    [Theory]
    [InlineData("573001234567", true)]
    [InlineData("3001234567", false)] // sin indicativo: leído como +30 (Grecia), no existe
    public void OnlyARealInternationalNumberIsDialable(string waId, bool expected) =>
        Assert.Equal(expected, new DialablePhoneNumbers(new LibPhoneNumberNormalizer()).IsDialable(waId));
}
