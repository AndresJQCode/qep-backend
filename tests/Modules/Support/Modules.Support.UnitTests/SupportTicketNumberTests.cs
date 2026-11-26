using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

public sealed class SupportTicketNumberTests
{
    [Theory]
    [InlineData(1L, "SOP-000001")]
    [InlineData(127L, "SOP-000127")]
    [InlineData(1_234_567L, "SOP-1234567")]
    public void FormatPadsToSixDigits(long number, string expected) =>
        Assert.Equal(expected, SupportTicketNumber.Format(number));

    [Theory]
    [InlineData("SOP-000127", 127L)]
    [InlineData("sop-127", 127L)]
    [InlineData("127", 127L)]
    [InlineData(" SOP-000127 ", 127L)]
    public void TryParseAcceptsTheRadicadoWithOrWithoutPrefix(string text, long expected)
    {
        Assert.True(SupportTicketNumber.TryParse(text, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("SOP-")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseRejectsWhatIsNotARadicado(string? text)
    {
        Assert.False(SupportTicketNumber.TryParse(text, out var number));
        Assert.Equal(0L, number);
    }
}
