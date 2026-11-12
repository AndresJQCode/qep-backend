using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerEmailTests
{
    [Theory, InlineData("  Ana@Correo.COM ", "ana@correo.com")]
    public void TryNormalizeTrimsAndLowercases(string raw, string expected)
    {
        Assert.True(BuyerEmail.TryNormalize(raw, out var n));
        Assert.Equal(expected, n);
    }

    [Theory, InlineData(""), InlineData("   "), InlineData("sin-arroba")]
    public void TryNormalizeRejectsMalformed(string raw) => Assert.False(BuyerEmail.TryNormalize(raw, out _));

    [Fact]
    public void TryNormalizeRejectsNull() => Assert.False(BuyerEmail.TryNormalize(null, out _));

    [Fact]
    public void TryNormalizeRejectsOverTheMaxLength()
    {
        var tooLong = new string('a', BuyerEmail.MaxLength - 5) + "@b.com";
        Assert.False(BuyerEmail.TryNormalize(tooLong, out _));
    }
}
