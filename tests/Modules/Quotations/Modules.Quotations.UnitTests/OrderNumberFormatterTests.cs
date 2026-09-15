using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El número de pedido (spec 2026-09-14, D2): prefijo PED- con el mismo contador que emitía los
/// VEN-, y al menos cuatro dígitos. Los VEN- ya emitidos no se reescriben.
/// </summary>
public sealed class OrderNumberFormatterTests
{
    [Theory]
    [InlineData(2026, 1L, "PED-2026-0001")]
    [InlineData(2026, 42L, "PED-2026-0042")]
    [InlineData(2027, 12345L, "PED-2027-12345")]
    public void FormatsWithThePedPrefix(int year, long sequence, string expected) =>
        Assert.Equal(expected, OrderNumberFormatter.Format(year, sequence));
}
