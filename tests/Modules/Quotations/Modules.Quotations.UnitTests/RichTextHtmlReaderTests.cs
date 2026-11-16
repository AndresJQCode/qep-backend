using BuildingBlocks.Application.RichText;
using BuildingBlocks.Infrastructure.RichText;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El lector del HTML saneado al árbol de bloques que pinta la plantilla del PDF (spec 2026-11-16 §5.3). Vive acá porque Quotations es
/// su consumidor y BuildingBlocks no tiene proyecto de pruebas. Los records llevan listas, que comparan por referencia: las
/// aserciones miran campo por campo.
/// </summary>
public sealed class RichTextHtmlReaderTests
{
    private static readonly RichTextHtmlReader Reader = new();

    [Fact]
    public void AParagraphWithMarksBecomesRuns()
    {
        var blocks = Reader.Read("<p>Hola <strong>mundo</strong> <em>y <u>más</u></em></p>");

        var paragraph = Assert.Single(blocks);
        Assert.Equal("paragraph", paragraph.Kind);
        Assert.Collection(
            paragraph.Runs!,
            run => Assert.Equal(new RichTextRun("Hola "), run),
            run => Assert.Equal(new RichTextRun("mundo", Bold: true), run),
            run => Assert.Equal(new RichTextRun(" "), run),
            run => Assert.Equal(new RichTextRun("y ", Italic: true), run),
            run => Assert.Equal(new RichTextRun("más", Italic: true, Underline: true), run));
    }

    [Fact]
    public void ABreakIsARun()
    {
        var blocks = Reader.Read("<p>a<br>b</p>");

        var paragraph = Assert.Single(blocks);
        Assert.Collection(
            paragraph.Runs!,
            run => Assert.Equal(new RichTextRun("a"), run),
            run => Assert.Equal(new RichTextRun("", Break: true), run),
            run => Assert.Equal(new RichTextRun("b"), run));
    }

    [Fact]
    public void HeadingsKeepTheirLevel()
    {
        var blocks = Reader.Read("<h3>X</h3>");

        var heading = Assert.Single(blocks);
        Assert.Equal("heading", heading.Kind);
        Assert.Equal(3, heading.Level);
        Assert.Equal(new RichTextRun("X"), Assert.Single(heading.Runs!));
    }

    [Fact]
    public void NestedListsBecomeItems()
    {
        var blocks = Reader.Read("<ul><li>a<ul><li>b</li></ul></li></ul>");

        var list = Assert.Single(blocks);
        Assert.Equal("list", list.Kind);
        Assert.False(list.Ordered);
        var item = Assert.Single(list.Items!);
        Assert.Collection(
            item,
            paragraph =>
            {
                Assert.Equal("paragraph", paragraph.Kind);
                Assert.Equal(new RichTextRun("a"), Assert.Single(paragraph.Runs!));
            },
            nested =>
            {
                Assert.Equal("list", nested.Kind);
                var nestedItem = Assert.Single(nested.Items!);
                var paragraph = Assert.Single(nestedItem);
                Assert.Equal("paragraph", paragraph.Kind);
                Assert.Equal(new RichTextRun("b"), Assert.Single(paragraph.Runs!));
            });
    }

    [Fact]
    public void ALinkCarriesItsHref()
    {
        var blocks = Reader.Read("<p><a href=\"https://x.co\">x</a></p>");

        var paragraph = Assert.Single(blocks);
        Assert.Equal(new RichTextRun("x", Href: "https://x.co"), Assert.Single(paragraph.Runs!));
    }

    // Defensa en profundidad: aunque el HTML guardado ya pasó por el saneador, el lector sólo enlaza https, mailto y tel.
    [Fact]
    public void AHrefOutsideTheAllowedSchemesIsDropped()
    {
        var blocks = Reader.Read("<p><a href=\"javascript:alert(1)\">x</a></p>");

        var paragraph = Assert.Single(blocks);
        var run = Assert.Single(paragraph.Runs!);
        Assert.Equal("x", run.Text);
        Assert.Null(run.Href);
    }

    [Fact]
    public void AdjacentRunsWithTheSameMarksMerge()
    {
        var blocks = Reader.Read("<p><strong>{{em</strong><strong>presa}}</strong></p>");

        var paragraph = Assert.Single(blocks);
        Assert.Equal(new RichTextRun("{{empresa}}", Bold: true), Assert.Single(paragraph.Runs!));
    }

    [Fact]
    public void MapTextReplacesEveryRunText()
    {
        var blocks = Reader.Read("<p>a<br>b</p><blockquote>c</blockquote><ol><li><em>d</em></li></ol><hr>");

        var mapped = RichText.MapText(blocks, text => text.ToUpperInvariant());

        Assert.Collection(
            mapped,
            paragraph => Assert.Collection(
                paragraph.Runs!,
                run => Assert.Equal(new RichTextRun("A"), run),
                run => Assert.Equal(new RichTextRun("", Break: true), run),
                run => Assert.Equal(new RichTextRun("B"), run)),
            quote =>
            {
                Assert.Equal("quote", quote.Kind);
                var inner = Assert.Single(quote.Children!);
                Assert.Equal(new RichTextRun("C"), Assert.Single(inner.Runs!));
            },
            list =>
            {
                Assert.True(list.Ordered);
                var paragraph = Assert.Single(Assert.Single(list.Items!));
                Assert.Equal(new RichTextRun("D", Italic: true), Assert.Single(paragraph.Runs!));
            },
            rule => Assert.Equal("rule", rule.Kind));
    }

    [Fact]
    public void AnEmptyParagraphIsSkipped()
    {
        var blocks = Reader.Read("<p></p><p>a</p>");

        var paragraph = Assert.Single(blocks);
        Assert.Equal(new RichTextRun("a"), Assert.Single(paragraph.Runs!));
    }
}
