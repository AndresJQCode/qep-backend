using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Legal;

namespace Modules.Support.UnitTests;

// Final review S1: the sanitizer's output is cached by the SHA-256 of the stored content, never by version id, so a row edited by hand
// is sanitized again.
public sealed class CachingSupportHtmlSanitizerTests
{
    [Fact]
    public void SanitizesTheSameContentOnce()
    {
        var inner = new CountingSanitizer();
        using var sanitizer = new CachingSupportHtmlSanitizer(inner);

        var first = sanitizer.Sanitize("<p>uno</p>");
        var second = sanitizer.Sanitize("<p>uno</p>");

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ContentChangedByHandIsSanitizedAgain()
    {
        var inner = new CountingSanitizer();
        using var sanitizer = new CachingSupportHtmlSanitizer(inner);

        sanitizer.Sanitize("<p>uno</p>");
        var edited = sanitizer.Sanitize("<p>uno</p><script>x</script>");

        Assert.Equal(2, inner.Calls);
        Assert.Equal("clean:<p>uno</p><script>x</script>", edited.Html);
    }

    [Fact]
    public void ARejectionIsNotCached()
    {
        var inner = new CountingSanitizer { Throw = true };
        using var sanitizer = new CachingSupportHtmlSanitizer(inner);

        Assert.Throws<InvalidOperationException>(() => sanitizer.Sanitize("<p>x</p>"));
        Assert.Throws<InvalidOperationException>(() => sanitizer.Sanitize("<p>x</p>"));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void GivesTheRealSanitizerOutput()
    {
        using var sanitizer = new CachingSupportHtmlSanitizer(new HtmlSupportSanitizer());

        var cached = sanitizer.Sanitize("<p onclick=\"x\">a</p><script>alert(1)</script>");

        Assert.Equal(new HtmlSupportSanitizer().Sanitize("<p onclick=\"x\">a</p><script>alert(1)</script>"), cached);
        Assert.Equal(cached, sanitizer.Sanitize("<p onclick=\"x\">a</p><script>alert(1)</script>"));
    }

    private sealed class CountingSanitizer : ISupportHtmlSanitizer
    {
        public int Calls { get; private set; }

        public bool Throw { get; init; }

        public SanitizedHtml Sanitize(string html)
        {
            Calls++;
            return Throw ? throw new InvalidOperationException("rejected") : new SanitizedHtml("clean:" + html, HasVisibleText: true);
        }
    }
}
