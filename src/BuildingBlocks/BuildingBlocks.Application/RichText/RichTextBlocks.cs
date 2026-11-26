namespace BuildingBlocks.Application.RichText;

/// <summary>
/// Un tramo de texto con sus marcas (spec 2026-11-16 §5.3). <see cref="Break"/> es un <c>&lt;br&gt;</c>: su <see cref="Text"/> va
/// vacío y <see cref="RichTextTree.MapText"/> no lo toca. Viaja a la plantilla del PDF en camelCase (<c>JsonSerializerDefaults.Web</c>),
/// y la plantilla pinta <see cref="Text"/> como cadena, nunca como markup.
/// </summary>
public sealed record RichTextRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false,
    string? Href = null,
    bool Break = false);

/// <summary>
/// Un bloque del árbol que sale del HTML saneado (spec 2026-11-16 §5.3). <see cref="Kind"/> es uno de <see cref="RichTextBlockKind"/>:
/// <c>paragraph</c> y <c>heading</c> (<see cref="Level"/> 2–4) llevan <see cref="Runs"/>; <c>list</c> lleva <see cref="Ordered"/> e
/// <see cref="Items"/>, donde cada ítem es a su vez una lista de bloques (así una lista se anida dentro de un ítem); <c>quote</c> lleva
/// <see cref="Children"/>; <c>rule</c> no lleva nada.
/// </summary>
public sealed record RichTextBlock(
    string Kind,
    int Level = 0,
    bool Ordered = false,
    IReadOnlyList<RichTextRun>? Runs = null,
    IReadOnlyList<IReadOnlyList<RichTextBlock>>? Items = null,
    IReadOnlyList<RichTextBlock>? Children = null);

/// <summary>Los valores de <see cref="RichTextBlock.Kind"/>. Son contrato con la plantilla del PDF: no se renombran.</summary>
public static class RichTextBlockKind
{
    public const string Paragraph = "paragraph";
    public const string Heading = "heading";
    public const string List = "list";
    public const string Quote = "quote";
    public const string Rule = "rule";
}

/// <summary>
/// Lee HTML <b>ya saneado</b> (la salida de <see cref="IRichTextSanitizer"/>) y lo convierte en bloques. No valida ni sanea: un HTML
/// que no pasó por el saneador puede traer profundidad o elementos que el lector no espera.
/// </summary>
public interface IRichTextReader
{
    IReadOnlyList<RichTextBlock> Read(string sanitizedHtml);
}

public static class RichTextTree
{
    /// <summary>
    /// Aplica <paramref name="map"/> al texto de cada tramo, en todo el árbol, y devuelve un árbol nuevo. Es como se resuelven las
    /// variables del contenido (spec 2026-11-16 §5.3): sobre el texto de los tramos, no sobre el HTML. Un salto de línea queda igual.
    /// </summary>
    public static IReadOnlyList<RichTextBlock> MapText(IReadOnlyList<RichTextBlock> blocks, Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(map);
        return blocks.Select(block => MapBlock(block, map)).ToList();
    }

    private static RichTextBlock MapBlock(RichTextBlock block, Func<string, string> map) =>
        block with
        {
            Runs = block.Runs?.Select(run => run.Break ? run : run with { Text = map(run.Text) }).ToList(),
            Items = block.Items?.Select(item => MapText(item, map)).ToList(),
            Children = block.Children is null ? null : MapText(block.Children, map),
        };
}
