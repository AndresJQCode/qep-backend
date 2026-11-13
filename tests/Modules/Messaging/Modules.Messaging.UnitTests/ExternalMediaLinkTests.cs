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
    [InlineData("https://user:pass@cdn.example.com/logo.png")]
    [InlineData("https://user@cdn.example.com/logo.png")]
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

    [Theory]
    [InlineData("a/b.pdf")]
    [InlineData("a\\b.pdf")]
    [InlineData("fac\u0001.pdf")]
    [InlineData("")]
    [InlineData(null)]
    public void AFilenameOutsideTheContractIsRejected(string? name) => Assert.False(ExternalMediaLink.IsValidFileName(name));

    [Fact]
    public void AFilenameOf241IsRejectedAnd240IsAccepted()
    {
        Assert.True(ExternalMediaLink.IsValidFileName(new string('a', 240)));
        Assert.False(ExternalMediaLink.IsValidFileName(new string('a', 241)));
    }

    [Fact]
    public void ARegularFilenameIsAccepted() => Assert.True(ExternalMediaLink.IsValidFileName("Factura-1.pdf"));

    [Fact]
    public void TheHeaderNeverPrintsItsLink()
    {
        var link = "https://cdn.example.com/secret-token.png";

        var text = TemplateHeader.ByLink("image", link, null).ToString();

        Assert.DoesNotContain(link, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.Contains("image", text, StringComparison.Ordinal);
        Assert.DoesNotContain(link, new ExternalTemplateHeader("image", link, null).ToString(), StringComparison.Ordinal);
    }
}
