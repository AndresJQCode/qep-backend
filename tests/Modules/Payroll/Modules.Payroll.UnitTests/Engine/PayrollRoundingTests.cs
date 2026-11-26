using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class PayrollRoundingTests
{
    [Fact]
    public void ToPesoRoundsHalfUp() => Assert.Equal(875_453m, PayrollRounding.ToPeso(875_452.5m));

    [Theory]
    [InlineData(35_018.12, 35_100)]
    [InlineData(35_000, 35_000)]
    [InlineData(35_000.01, 35_100)]
    public void CeilingToHundredNeverRoundsDown(double value, int expected) =>
        Assert.Equal((decimal)expected, PayrollRounding.CeilingToHundred((decimal)value));

    [Fact]
    public void CeilingToPeso() => Assert.Equal(875_453m, PayrollRounding.CeilingToPeso(875_452.01m));
}
