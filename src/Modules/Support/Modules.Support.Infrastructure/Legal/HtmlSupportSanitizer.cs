using AngleSharp.Dom;
using Ganss.Xss;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure.Legal;

/// <summary>
/// <see cref="ISupportHtmlSanitizer"/> on <c>HtmlSanitizer</c> (mganss, on AngleSharp), with the allowlist of spec 2026-11-15 §6.2
/// (plan L2 and L3). Registered as a singleton but stateless: the library exposes events and mutable sets, so each call builds its own
/// <see cref="HtmlSanitizer"/> (<see cref="Create"/>). At most three documents per request: the cost is negligible.
/// </summary>
internal sealed class HtmlSupportSanitizer : ISupportHtmlSanitizer
{
    private const string LinkRel = "noopener noreferrer nofollow";

    private static readonly string[] AllowedTags =
        ["p", "br", "strong", "em", "u", "s", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "hr", "a"];

    private static readonly string[] AllowedSchemes = ["https", "mailto", "tel"];

    // Removed with their content before sanitizing. KeepChildNodes would otherwise leave the code of a <script> as visible text, and
    // foreign content (svg, math) and raw-text elements are where mutation XSS lives.
    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math", "form", "input", "img", "video",
        "audio",
    };

    public SanitizedHtml Sanitize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var sanitizer = Create();
        // The same document HtmlSanitizer.Sanitize builds, parsed once: the dangerous elements leave the DOM before the library walks
        // it, and nothing is serialized and parsed again in between.
        using var document = sanitizer.HtmlParserFactory().ParseDocument("<!doctype html><html><body>" + html);
        var body = document.Body!;
        DropDangerousNodes(body);
        sanitizer.SanitizeDom(document, body, string.Empty);

        using var writer = new StringWriter();
        foreach (var node in body.ChildNodes)
        {
            node.ToHtml(writer, sanitizer.OutputFormatter);
        }

        return new SanitizedHtml(writer.ToString(), !string.IsNullOrWhiteSpace(body.TextContent));
    }

    // Plan L3: the library's sets are emptied and loaded with the allowlist; no CSS, no classes, no data-*; unknown elements are
    // unwrapped (KeepChildNodes). rel and target are set after sanitizing, so a second pass removes them and puts them back the same.
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
            // The library keeps relative URLs (no base URL): only an absolute https, mailto or tel survives.
            if (args.SanitizedUrl is not { } url
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
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
