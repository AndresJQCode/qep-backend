using AngleSharp.Dom;
using BuildingBlocks.Application.RichText;
using FluentValidation;
using FluentValidation.Results;
using Ganss.Xss;

namespace BuildingBlocks.Infrastructure.RichText;

/// <summary>
/// <see cref="IRichTextSanitizer"/> sobre <c>HtmlSanitizer</c> (mganss, sobre AngleSharp), con la allowlist del spec 2026-11-15 §6.2
/// (plan L2 y L3). Nació en Soporte para los documentos legales y se movió acá para que lo compartan con el contenido del PDF de
/// cotizaciones (spec 2026-11-16 §5.2). Se puede registrar como singleton porque no tiene estado: la librería expone eventos y
/// conjuntos mutables, así que cada llamada arma su propio <see cref="HtmlSanitizer"/> (<see cref="Create"/>).
/// <para>
/// Idempotente por construcción: el parser de HTML puede reestructurar lo que deja el desenvolver (<c>&lt;h2&gt;&lt;b&gt;&lt;h3&gt;</c>
/// queda <c>&lt;h2&gt;&lt;h3&gt;</c> tras una pasada y <c>&lt;h2&gt;&lt;/h2&gt;&lt;h3&gt;</c> al parsearlo otra vez), así que la pasada se
/// repite hasta que su salida es un punto fijo. Un HTML demasiado grande, demasiado profundo o que no se estabiliza se rechaza como
/// <c>validation.failed</c> sobre la clave que te pasen: la librería y AngleSharp recorren el DOM de forma recursiva, y un documento lo
/// bastante profundo desbordaría la pila.
/// </para>
/// </summary>
public sealed class RichTextHtmlSanitizer : IRichTextSanitizer
{
    private const string LinkRel = "noopener noreferrer nofollow";

    private static readonly string[] AllowedTags =
        ["p", "br", "strong", "em", "u", "s", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "hr", "a"];

    private static readonly string[] AllowedSchemes = ["https", "mailto", "tel"];

    /// <summary>Si <paramref name="url"/> es un enlace que el saneador deja pasar: absoluto y con esquema <c>https</c>, <c>mailto</c> o
    /// <c>tel</c>. Lo usa también <see cref="RichTextHtmlReader"/>, para no repetir la lista.</summary>
    internal static bool IsAllowedLink(string? url) =>
        url is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    // Se quitan con su contenido antes de sanear. Con KeepChildNodes, el código de un <script> quedaría como texto visible, y el
    // contenido foráneo (svg, math) y los elementos de texto crudo son donde vive el XSS por mutación.
    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math", "form", "input", "img", "video",
        "audio", "textarea", "title", "select", "option", "button", "noembed", "noframes", "xmp", "plaintext", "applet", "frameset",
    };

    // Todo lo que el parser reestructura una vez llega a punto fijo en dos pasadas; la tercera es el margen.
    private const int MaxPasses = 3;

    // Un documento legal o el contenido de un PDF tiene pocos niveles y queda muy por debajo; los límites alejan del borde de la pila
    // los recorridos recursivos del DOM.
    public const int MaxDepth = 100;
    public const int MaxElements = 10_000;

    public RichTextSanitized Sanitize(string html, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (CountStartTags(html) > MaxElements)
        {
            throw Rejected(propertyName, $"The content has more than {MaxElements} elements.");
        }

        var current = Pass(html, propertyName);
        for (var pass = 1; pass < MaxPasses; pass++)
        {
            var next = Pass(current.Html, propertyName);
            if (string.Equals(next.Html, current.Html, StringComparison.Ordinal))
            {
                return next;
            }

            current = next;
        }

        throw Rejected(propertyName, "The content could not be sanitized to a stable form.");
    }

    private static RichTextSanitized Pass(string html, string propertyName)
    {
        var sanitizer = Create();
        // El mismo documento que arma HtmlSanitizer.Sanitize, parseado una sola vez: los elementos peligrosos salen del DOM antes de
        // que la librería lo recorra, y nada se serializa y se vuelve a parsear en el medio. El constructor del árbol es iterativo; la
        // profundidad se revisa antes de cualquier recorrido recursivo.
        using var document = sanitizer.HtmlParserFactory().ParseDocument("<!doctype html><html><body>" + html);
        var body = document.Body!;
        if (DepthOf(body) > MaxDepth)
        {
            throw Rejected(propertyName, $"The content is nested more than {MaxDepth} levels deep.");
        }

        DropDangerousNodes(body);
        sanitizer.SanitizeDom(document, body, string.Empty);

        using var writer = new StringWriter();
        foreach (var node in body.ChildNodes)
        {
            node.ToHtml(writer, sanitizer.OutputFormatter);
        }

        return new RichTextSanitized(writer.ToString(), !string.IsNullOrWhiteSpace(body.TextContent));
    }

    // Antes de parsear: todo '<' seguido de una letra abre un elemento (o es texto que el parser convierte en uno). Cota superior,
    // barata.
    private static int CountStartTags(string html)
    {
        var count = 0;
        for (var index = 0; index < html.Length - 1; index++)
        {
            if (html[index] == '<' && char.IsAsciiLetter(html[index + 1]))
            {
                count++;
            }
        }

        return count;
    }

    // Iterativo a propósito: es la guarda contra la recursión.
    private static int DepthOf(INode root)
    {
        var deepest = 0;
        var pending = new Stack<(INode Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            if (deepest > MaxDepth)
            {
                return deepest;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is IElement)
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return deepest;
    }

    private static ValidationException Rejected(string propertyName, string message) =>
        new([new ValidationFailure(propertyName, message)]);

    // Plan L3: los conjuntos de la librería se vacían y se cargan con la allowlist; sin CSS, sin clases, sin data-*; los elementos
    // desconocidos se desenvuelven (KeepChildNodes). rel y target se ponen después de sanear, así que una segunda pasada los quita y
    // los vuelve a poner iguales.
    private static HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer { KeepChildNodes = true, AllowDataAttributes = false };
        Reset(sanitizer.AllowedTags, AllowedTags);
        Reset(sanitizer.AllowedAttributes, ["href"]);
        Reset(sanitizer.AllowedSchemes, AllowedSchemes);
        Reset(sanitizer.UriAttributes, ["href"]);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.FilterUrl += (_, args) =>
        {
            // La librería conserva las URL relativas (no hay URL base): sólo sobrevive un https, mailto o tel absoluto.
            if (!IsAllowedLink(args.SanitizedUrl))
            {
                args.SanitizedUrl = null;
            }
        };
        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is IElement { LocalName: "a" } link && link.HasAttribute("href"))
            {
                link.SetAttribute("rel", LinkRel);
                link.SetAttribute("target", "_blank");
            }
        };
        return sanitizer;
    }

    private static void Reset<T>(ICollection<T> set, IEnumerable<T> values)
    {
        set.Clear();
        foreach (var value in values)
        {
            set.Add(value);
        }
    }

    private static void DropDangerousNodes(IElement body)
    {
        foreach (var element in body.Descendants<IElement>().Where(item => DroppedWithContent.Contains(item.LocalName)).ToList())
        {
            element.Remove();
        }

        foreach (var comment in body.Descendants<IComment>().ToList())
        {
            comment.Remove();
        }
    }
}
