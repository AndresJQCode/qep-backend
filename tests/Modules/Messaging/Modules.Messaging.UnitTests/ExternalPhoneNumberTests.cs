using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-30 §6.3.2: el número externo se normaliza sin adivinar país.</summary>
public sealed class ExternalPhoneNumberTests
{
    [Theory]
    [InlineData("573001234567", "573001234567")]
    [InlineData("+57 300 123 4567", "573001234567")]
    [InlineData("+57-300-123-4567", "573001234567")]
    [InlineData("12345678", "12345678")]
    [InlineData("123456789012345", "123456789012345")]
    public void AcceptedForms(string raw, string expected)
    {
        Assert.True(ExternalPhoneNumber.TryNormalize(raw, out var waId));
        Assert.Equal(expected, waId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("1234567890123456")]
    [InlineData("(57) 3001234567")]
    [InlineData("0573001234567")]
    [InlineData("00573001234567")]
    [InlineData("٥٧٣٠٠١٢٣٤٥٦٧")]
    public void RejectedForms(string? raw)
    {
        Assert.False(ExternalPhoneNumber.TryNormalize(raw, out var waId));
        Assert.Equal(string.Empty, waId);
    }
}
