using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BuildingBlocks.Application.RichText;

namespace BuildingBlocks.Infrastructure.RichText;

/// <summary>
/// <see cref="IRichTextReader"/> sobre AngleSharp, el mismo parser del saneador (spec 2026-11-16 §5.3). Espera la salida de
/// <see cref="RichTextHtmlSanitizer"/>: sólo conoce su allowlist, y la profundidad acotada por el saneador es la que deja recorrer el
/// árbol de forma recursiva. Sin estado.
/// <para>
/// Reglas: el texto suelto (en el cuerpo, en un <c>li</c> o en un <c>blockquote</c>) va en un <c>paragraph</c>; los tramos contiguos
/// con las mismas marcas se funden en uno, así un token no queda partido por nodos de texto; <c>&lt;br&gt;</c> es un tramo
/// <see cref="RichTextRun.Break"/>; <c>a[href]</c> le pone <see cref="RichTextRun.Href"/> a sus tramos; un párrafo o un título sin
/// texto ni saltos se omite. El espacio en blanco se colapsa como lo hace el navegador al pintar HTML.
/// </para>
/// </summary>
public sealed partial class RichTextHtmlReader : IRichTextReader
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "hr",
    };

    private static readonly RichTextRun Plain = new(string.Empty);

    public IReadOnlyList<RichTextBlock> Read(string sanitizedHtml)
    {
        ArgumentNullException.ThrowIfNull(sanitizedHtml);
        using var document = new HtmlParser().ParseDocument("<!doctype html><html><body>" + sanitizedHtml);
        return ReadBlocks(document.Body!.ChildNodes);
    }

    // El contenido de un contenedor (cuerpo, li, blockquote): cada bloque sale tal cual y lo que hay entre bloques se junta en un
    // párrafo.
    private static List<RichTextBlock> ReadBlocks(IEnumerable<INode> nodes)
    {
        var blocks = new List<RichTextBlock>();
        var loose = new List<RichTextRun>();
        foreach (var node in nodes)
        {
            if (node is IElement element && BlockTags.Contains(element.LocalName))
            {
                AddParagraph(blocks, loose);
                loose = [];
                AddBlock(blocks, element);
            }
            else
            {
                CollectRuns(node, Plain, loose);
            }
        }

        AddParagraph(blocks, loose);
        return blocks;
    }

    private static void AddBlock(List<RichTextBlock> blocks, IElement element)
    {
        switch (element.LocalName)
        {
            case "p":
                AddParagraph(blocks, RunsOf(element));
                break;
            case "h2" or "h3" or "h4":
                var runs = RunsOf(element);
                if (HasContent(runs))
                {
                    blocks.Add(new RichTextBlock(RichTextBlockKind.Heading, Level: element.LocalName[1] - '0', Runs: runs));
                }

                break;
            case "ul" or "ol":
                var items = ReadItems(element);
                if (items.Count > 0)
                {
                    blocks.Add(new RichTextBlock(RichTextBlockKind.List, Ordered: element.LocalName == "ol", Items: items));
                }

                break;
            case "blockquote":
                var children = ReadBlocks(element.ChildNodes);
                if (children.Count > 0)
                {
                    blocks.Add(new RichTextBlock(RichTextBlockKind.Quote, Children: children));
                }

                break;
            case "hr":
                blocks.Add(new RichTextBlock(RichTextBlockKind.Rule));
                break;
            default:
                // Un li fuera de una lista: su contenido sale como bloques sueltos, sin viñeta.
                blocks.AddRange(ReadBlocks(element.ChildNodes));
                break;
        }
    }

    // Cada li es un ítem. Lo que el parser deje dentro de la lista fuera de un li no se pierde: va como un ítem propio.
    private static List<IReadOnlyList<RichTextBlock>> ReadItems(IElement list)
    {
        var items = new List<IReadOnlyList<RichTextBlock>>();
        var stray = new List<INode>();
        foreach (var child in list.ChildNodes)
        {
            if (child is IElement { LocalName: "li" } item)
            {
                AddItem(items, stray);
                stray = [];
                AddItem(items, item.ChildNodes);
            }
            else
            {
                stray.Add(child);
            }
        }

        AddItem(items, stray);
        return items;
    }

    private static void AddItem(List<IReadOnlyList<RichTextBlock>> items, IEnumerable<INode> nodes)
    {
        var blocks = ReadBlocks(nodes);
        if (blocks.Count > 0)
        {
            items.Add(blocks);
        }
    }

    private static List<RichTextRun> RunsOf(IElement element)
    {
        var runs = new List<RichTextRun>();
        foreach (var child in element.ChildNodes)
        {
            CollectRuns(child, Plain, runs);
        }

        return runs;
    }

    // style es un tramo vacío con las marcas que vienen de los ancestros; cada texto lo copia con su contenido.
    private static void CollectRuns(INode node, RichTextRun style, List<RichTextRun> runs)
    {
        switch (node)
        {
            case IText text:
                var collapsed = Whitespace().Replace(text.Data, " ");
                if (collapsed.Length > 0)
                {
                    Append(runs, style with { Text = collapsed });
                }

                return;
            case IElement { LocalName: "br" }:
                runs.Add(new RichTextRun(string.Empty, Break: true));
                return;
            case IElement element:
                var inner = element.LocalName switch
                {
                    "strong" or "b" => style with { Bold = true },
                    "em" or "i" => style with { Italic = true },
                    "u" => style with { Underline = true },
                    "s" => style with { Strike = true },
                    "a" when element.GetAttribute("href") is { } href => style with { Href = href },
                    _ => style,
                };
                foreach (var child in element.ChildNodes)
                {
                    CollectRuns(child, inner, runs);
                }

                return;
        }
    }

    // Funde con el anterior si tiene las mismas marcas: así {{em</strong><strong>presa}} queda un solo tramo y la variable se reconoce.
    private static void Append(List<RichTextRun> runs, RichTextRun run)
    {
        if (runs.Count > 0 && runs[^1] is { Break: false } last && last with { Text = run.Text } == run)
        {
            runs[^1] = last with { Text = last.Text + run.Text };
            return;
        }

        runs.Add(run);
    }

    private static void AddParagraph(List<RichTextBlock> blocks, List<RichTextRun> runs)
    {
        if (HasContent(runs))
        {
            blocks.Add(new RichTextBlock(RichTextBlockKind.Paragraph, Runs: runs));
        }
    }

    // Un párrafo con sólo espacios es el blanco entre bloques del HTML, no contenido; uno con un salto (<p><br></p>) es una línea en
    // blanco que el autor puso a propósito.
    private static bool HasContent(List<RichTextRun> runs) =>
        runs.Any(run => run.Break || !string.IsNullOrWhiteSpace(run.Text));

    // El espacio en blanco de HTML (no el &nbsp;, que es contenido).
    [GeneratedRegex("[ \t\n\r\f]+")]
    private static partial Regex Whitespace();
}
