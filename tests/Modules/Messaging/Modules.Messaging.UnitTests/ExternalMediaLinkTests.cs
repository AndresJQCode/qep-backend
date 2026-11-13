using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-13: la URL de un encabezado multimedia es https absoluto, sin espacios ni caracteres de control, de hasta
/// 2048 caracteres.</summary>
public sealed class ExternalMediaLinkTests
{
    [Theory]
    [InlineData("http://cdn.example.com/logo.png")]
    [InlineData("/logo.png")]
    [InlineData("cdn.example.com/logo.png")]
    [InlineData("ftp://cdn.example.com/logo.png")]
    [InlineData("https://cdn.example.com/lo go.png")]
    [InlineData("https://cdn.example.com/\u0007.png")]
    [InlineData("")]
    [InlineData(null)]
    public void ALinkOutsideTheContractIsRejected(string? link) => Assert.False(ExternalMediaLink.IsValidLink(link));

    [Fact]
    public void ALinkOf2049CharactersIsRejectedAnd2048IsAccepted()
    {
        var prefix = "https://cdn.example.com/";

        Assert.True(ExternalMediaLink.IsValidLink(prefix + new string('a', 2048 - prefix.Length)));
        Assert.False(ExternalMediaLink.IsValidLink(prefix + new string('a', 2049 - prefix.Length)));
    }

    [Fact]
    public void AnHttpsLinkWithAQueryIsAccepted() => Assert.True(ExternalMediaLink.IsValidLink("https://cdn.example.com/logo.png?token=abc"));
}
