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

        var mapped = RichTextTree.MapText(blocks, text => text.ToUpperInvariant());

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

    // El saneador deja pasar un inline que envuelve bloques: sus bloques se conservan y heredan las marcas.
    [Fact]
    public void ABlockInsideAnInlineKeepsItsBoundaries()
    {
        var blocks = Reader.Read("<strong><p>a</p><ul><li>b</li></ul></strong>");

        Assert.Collection(
            blocks,
            paragraph =>
            {
                Assert.Equal("paragraph", paragraph.Kind);
                Assert.Equal(new RichTextRun("a", Bold: true), Assert.Single(paragraph.Runs!));
            },
            list =>
            {
                Assert.Equal("list", list.Kind);
                var paragraph = Assert.Single(Assert.Single(list.Items!));
                Assert.Equal(new RichTextRun("b", Bold: true), Assert.Single(paragraph.Runs!));
            });
    }

    [Fact]
    public void ALinkWrappingParagraphsKeepsTheHref()
    {
        var blocks = Reader.Read("<a href=\"https://x.co\"><p>a</p><p>b</p></a>");

        Assert.Collection(
            blocks,
            first =>
            {
                Assert.Equal("paragraph", first.Kind);
                Assert.Equal(new RichTextRun("a", Href: "https://x.co"), Assert.Single(first.Runs!));
            },
            second =>
            {
                Assert.Equal("paragraph", second.Kind);
                Assert.Equal(new RichTextRun("b", Href: "https://x.co"), Assert.Single(second.Runs!));
            });
    }

    [Fact]
    public void WhitespaceCollapsesAcrossRuns()
    {
        var blocks = Reader.Read("<p> a <b> b</b> </p>");

        var paragraph = Assert.Single(blocks);
        Assert.Collection(
            paragraph.Runs!,
            run => Assert.Equal(new RichTextRun("a "), run),
            run => Assert.Equal(new RichTextRun("b", Bold: true), run));
    }

    [Fact]
    public void WhitespaceAroundABreakIsTrimmed()
    {
        var blocks = Reader.Read("<p>a <br> b</p>");

        var paragraph = Assert.Single(blocks);
        Assert.Collection(
            paragraph.Runs!,
            run => Assert.Equal(new RichTextRun("a"), run),
            run => Assert.Equal(new RichTextRun("", Break: true), run),
            run => Assert.Equal(new RichTextRun("b"), run));
    }

    [Fact]
    public void AnOrderedListIsOrdered()
    {
        var blocks = Reader.Read("<ol><li>a</li></ol>");

        var list = Assert.Single(blocks);
        Assert.Equal("list", list.Kind);
        Assert.True(list.Ordered);
        var paragraph = Assert.Single(Assert.Single(list.Items!));
        Assert.Equal(new RichTextRun("a"), Assert.Single(paragraph.Runs!));
    }

    [Fact]
    public void AQuoteHoldsItsParagraphs()
    {
        var blocks = Reader.Read("<blockquote><p>a</p></blockquote>");

        var quote = Assert.Single(blocks);
        Assert.Equal("quote", quote.Kind);
        var paragraph = Assert.Single(quote.Children!);
        Assert.Equal("paragraph", paragraph.Kind);
        Assert.Equal(new RichTextRun("a"), Assert.Single(paragraph.Runs!));
    }

    [Fact]
    public void ABreakInsideAHeadingIsKept()
    {
        var blocks = Reader.Read("<h2>a<br>b</h2>");

        var heading = Assert.Single(blocks);
        Assert.Equal("heading", heading.Kind);
        Assert.Equal(2, heading.Level);
        Assert.Collection(
            heading.Runs!,
            run => Assert.Equal(new RichTextRun("a"), run),
            run => Assert.Equal(new RichTextRun("", Break: true), run),
            run => Assert.Equal(new RichTextRun("b"), run));
    }

    [Fact]
    public void MailtoAndTelLinksAreKept()
    {
        var blocks = Reader.Read("<p><a href=\"mailto:a@b.co\">correo</a> <a href=\"tel:+573001234567\">celular</a></p>");

        var paragraph = Assert.Single(blocks);
        Assert.Collection(
            paragraph.Runs!,
            run => Assert.Equal(new RichTextRun("correo", Href: "mailto:a@b.co"), run),
            run => Assert.Equal(new RichTextRun(" "), run),
            run => Assert.Equal(new RichTextRun("celular", Href: "tel:+573001234567"), run));
    }
}
