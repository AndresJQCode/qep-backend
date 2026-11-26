using FluentValidation;
using Modules.Support.Infrastructure.Legal;

namespace Modules.Support.UnitTests;

// The allowlist of the legal documents (spec 2026-11-15 §6.2, plan L2 and L3), against the real HtmlSanitizer.
public sealed class SupportHtmlSanitizerTests
{
    private const string Rel = "rel=\"noopener noreferrer nofollow\" target=\"_blank\"";

    private static readonly HtmlSupportSanitizer Sanitizer = new();

    private static string Clean(string html) => Sanitizer.Sanitize(html).Html;

    public static TheoryData<string> Outputs() =>
    [
        Clean(EveryAllowedElement),
        Clean("<p>a</p><script>alert(1)</script><style>p{color:red}</style><iframe src=\"https://x\">i</iframe>"),
        Clean("<div><span>uno</span> <b>dos</b> <i>tres</i> <font color=\"red\">cuatro</font></div>"),
        Clean("<p style=\"color:red\" class=\"c\" id=\"i\" data-x=\"1\" onclick=\"alert(1)\">a</p><img src=x onerror=alert(1)>"),
        Clean("<a href=\"https://x.co\" rel=\"opener\" target=\"_self\">x</a>"),
        Clean("<a href=\"javascript:alert(1)\">x</a><a href=\"mailto:a@b.co\">m</a><a href=\"tel:+573001234567\">t</a>"),
        Clean("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>"),
        Clean("<svg><style><img src=x onerror=alert(1)></style></svg>"),
        Clean("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>"),
        Clean("<p>a &amp; b &lt; c &gt; d \"e\" 'f'&nbsp;g</p><!-- comment --><p>x<br>y</p>"),
        // Unwrapping can leave markup the parser restructures when it reads it again: the sanitizer runs to a fixed point.
        Clean("<h2><b><h3>x</h3></b></h2>"),
        Clean("<p><a href=\"https://x.co\">uno <span><a href=\"https://y.co\">dos</a></span> tres</a></p>"),
        Clean("<ul><li>a<span><li>b</li></span></li></ul><p><b><p>c</p></b></p>"),
    ];

    private const string EveryAllowedElement =
        "<h2>Título</h2><h3>Sub</h3><h4>Subsub</h4>"
        + "<p>Uno <strong>fuerte</strong> <em>énfasis</em> <u>subrayado</u> <s>tachado</s><br>dos</p>"
        + "<ul><li>a</li></ul><ol><li>b</li></ol>"
        + "<blockquote>cita</blockquote><hr>"
        + "<p><a href=\"https://example.com/politica\" " + Rel + ">enlace</a></p>";

    [Fact]
    public void KeepsEveryAllowedElement() => Assert.Equal(EveryAllowedElement, Clean(EveryAllowedElement));

    [Theory]
    [InlineData("script", "<script>secreto</script>")]
    [InlineData("style", "<style>secreto</style>")]
    [InlineData("iframe", "<iframe>secreto</iframe>")]
    [InlineData("object", "<object>secreto</object>")]
    [InlineData("embed", "<embed src=\"secreto\">")]
    [InlineData("svg", "<svg><text>secreto</text></svg>")]
    [InlineData("math", "<math><mi>secreto</mi></math>")]
    [InlineData("template", "<template>secreto</template>")]
    [InlineData("noscript", "<noscript>secreto</noscript>")]
    [InlineData("form", "<form>secreto</form>")]
    [InlineData("input", "<input value=\"secreto\">")]
    [InlineData("img", "<img src=\"https://x.co/a.png\" alt=\"secreto\">")]
    [InlineData("video", "<video>secreto</video>")]
    [InlineData("audio", "<audio>secreto</audio>")]
    public void DropsDangerousElementsWithTheirContent(string tag, string element)
    {
        var output = Clean($"<p>antes</p>{element}<p>después</p>");

        Assert.Equal("<p>antes</p><p>después</p>", output);
        Assert.DoesNotContain("secreto", output, StringComparison.Ordinal);
        Assert.DoesNotContain(tag, output, StringComparison.OrdinalIgnoreCase);
    }

    // A <div> cannot live inside a <p>: the HTML parser closes the <p> when the <div> opens (what a browser builds too), so the
    // block case is wrapped in an element that takes flow content. The sanitizer must keep parsing like a browser; it is an XSS boundary.
    [Theory]
    [InlineData("span", "p")]
    [InlineData("div", "blockquote")]
    [InlineData("b", "p")]
    [InlineData("i", "p")]
    [InlineData("font", "p")]
    public void UnwrapsUnknownElementsKeepingTheirText(string tag, string container) =>
        Assert.Equal(
            $"<{container}>uno dos tres</{container}>", Clean($"<{container}>uno <{tag}>dos</{tag}> tres</{container}>"));

    [Theory]
    [InlineData("style=\"color:red\"")]
    [InlineData("class=\"c\"")]
    [InlineData("id=\"i\"")]
    [InlineData("data-x=\"1\"")]
    [InlineData("onclick=\"alert(1)\"")]
    [InlineData("onerror=\"alert(1)\"")]
    public void DropsAttributesOtherThanHref(string attribute) =>
        Assert.Equal("<p>a</p>", Clean($"<p {attribute}>a</p>"));

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt:alert(1)", false)]
    [InlineData("java&#x09;script:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("\u0001javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("http://x.co", false)]
    [InlineData("/relativa", false)]
    [InlineData("#ancla", false)]
    [InlineData("https://x.co", true)]
    [InlineData("mailto:a@b.co", true)]
    [InlineData("tel:+573001234567", true)]
    public void KeepsOnlyHttpsMailtoAndTelLinks(string href, bool kept)
    {
        var output = Clean($"<p><a href=\"{href}\">x</a></p>");

        if (kept)
        {
            Assert.Equal($"<p><a href=\"{href}\" {Rel}>x</a></p>", output);
        }
        else
        {
            Assert.DoesNotContain("href", output, StringComparison.Ordinal);
            Assert.DoesNotContain("script", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(">x</a>", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LinksGetRelAndTarget()
    {
        Assert.Equal($"<a href=\"https://x.co\" {Rel}>x</a>", Clean("<a href=\"https://x.co\">x</a>"));
        Assert.Equal(
            $"<a href=\"https://x.co\" {Rel}>x</a>",
            Clean("<a target=\"_self\" rel=\"opener\" href=\"https://x.co\">x</a>"));
    }

    [Theory]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>")]
    [InlineData("<svg><style><img src=x onerror=alert(1)></style></svg>")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>")]
    public void ResistsMutationXss(string html)
    {
        var output = Clean(html);

        Assert.DoesNotContain("onerror", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Outputs))]
    public void SanitizingTwiceGivesTheSameBytes(string once) => Assert.Equal(once, Clean(once));

    [Fact]
    public void AHeadingInsideAnUnwrappedElementSettlesInOneCall() =>
        Assert.Equal("<h2></h2><h3>x</h3>", Clean("<h2><b><h3>x</h3></b></h2>"));

    [Fact]
    public void TooManyElementsAreRejectedNotACrash()
    {
        var deep = string.Concat(Enumerable.Repeat("<blockquote>", 20_000)) + "x"
            + string.Concat(Enumerable.Repeat("</blockquote>", 20_000));

        var error = Assert.Throws<ValidationException>(() => Sanitizer.Sanitize(deep));

        Assert.Equal("ContentHtml", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public void TooDeepANestingIsRejected()
    {
        var deep = string.Concat(Enumerable.Repeat("<blockquote>", HtmlSupportSanitizer.MaxDepth + 50)) + "x";

        var error = Assert.Throws<ValidationException>(() => Sanitizer.Sanitize(deep));

        Assert.Equal("ContentHtml", Assert.Single(error.Errors).PropertyName);
    }

    [Theory]
    [InlineData("textarea", "<textarea>secreto</textarea>")]
    [InlineData("title", "<title>secreto</title>")]
    [InlineData("select", "<select><option>secreto</option></select>")]
    [InlineData("button", "<button>secreto</button>")]
    [InlineData("noembed", "<noembed>secreto</noembed>")]
    [InlineData("noframes", "<noframes>secreto</noframes>")]
    [InlineData("xmp", "<xmp>secreto</xmp>")]
    [InlineData("applet", "<applet>secreto</applet>")]
    public void DropsFormAndRawTextElementsWithTheirContent(string tag, string element)
    {
        var output = Clean($"<p>antes</p>{element}<p>después</p>");

        Assert.DoesNotContain("secreto", output, StringComparison.Ordinal);
        Assert.DoesNotContain(tag, output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<p></p>", false)]
    [InlineData("<p>&nbsp;</p>", false)]
    [InlineData("<br>", false)]
    [InlineData("<script>x</script>", false)]
    [InlineData("   ", false)]
    [InlineData("<p>a</p>", true)]
    public void HasVisibleTextIsFalseForEmptyMarkup(string html, bool visible) =>
        Assert.Equal(visible, Sanitizer.Sanitize(html).HasVisibleText);

    // Plan L12: what the migration writes from the plain texts of phase 3 (escape only &, < and >, <p> per blank line, <br> per
    // single break). If this fails, the backfill is not canonical and GET /form would change the bytes the tenant published.
    [Theory]
    [InlineData("<p>Datos &amp; política</p>")]
    [InlineData("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>")]
    [InlineData("<p>Dijo \"hola\" y 'adiós'</p>")]
    [InlineData("<p>uno<br>dos</p>")]
    [InlineData("<p>uno</p><p>dos</p>")]
    [InlineData("<p>uno</p><p>dos<br>tres</p><p>cuatro &amp; &lt;cinco&gt;</p>")]
    public void BackfilledHtmlIsAlreadyCanonical(string backfilled) => Assert.Equal(backfilled, Clean(backfilled));
}
