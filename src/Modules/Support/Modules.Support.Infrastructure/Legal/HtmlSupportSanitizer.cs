using AngleSharp.Dom;
using FluentValidation;
using FluentValidation.Results;
using Ganss.Xss;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Legal;

/// <summary>
/// <see cref="ISupportHtmlSanitizer"/> on <c>HtmlSanitizer</c> (mganss, on AngleSharp), with the allowlist of spec 2026-11-15 §6.2
/// (plan L2 and L3). Registered as a singleton but stateless: the library exposes events and mutable sets, so each call builds its own
/// <see cref="HtmlSanitizer"/> (<see cref="Create"/>). At most three documents per request: the cost is negligible.
/// <para>
/// Idempotent by construction: the HTML parser can restructure what unwrapping leaves (<c>&lt;h2&gt;&lt;b&gt;&lt;h3&gt;</c> becomes
/// <c>&lt;h2&gt;&lt;h3&gt;</c> after one pass and <c>&lt;h2&gt;&lt;/h2&gt;&lt;h3&gt;</c> when parsed again), so the pass runs until its
/// output is a fixed point. Markup that is too large, too deep or does not settle is rejected as <c>validation.failed</c> on
/// <c>ContentHtml</c>: the library and AngleSharp walk the DOM recursively, and a deep enough document would overflow the stack.
/// </para>
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
        "audio", "textarea", "title", "select", "option", "button", "noembed", "noframes", "xmp", "plaintext", "applet", "frameset",
    };

    // A fixed point is reached in two passes for everything the parser restructures once; a third is the margin.
    private const int MaxPasses = 3;

    // A legal document is a few levels deep and well under these; the limits keep the recursive DOM walks off the stack's edge.
    internal const int MaxDepth = 100;
    internal const int MaxElements = 10_000;

    public SanitizedHtml Sanitize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (CountStartTags(html) > MaxElements)
        {
            throw Rejected($"The content has more than {MaxElements} elements.");
        }

        var current = Pass(html);
        for (var pass = 1; pass < MaxPasses; pass++)
        {
            var next = Pass(current.Html);
            if (string.Equals(next.Html, current.Html, StringComparison.Ordinal))
            {
                return next;
            }

            current = next;
        }

        throw Rejected("The content could not be sanitized to a stable form.");
    }

    private static SanitizedHtml Pass(string html)
    {
        var sanitizer = Create();
        // The same document HtmlSanitizer.Sanitize builds, parsed once: the dangerous elements leave the DOM before the library walks
        // it, and nothing is serialized and parsed again in between. The tree builder is iterative; the depth is checked before any
        // recursive walk.
        using var document = sanitizer.HtmlParserFactory().ParseDocument("<!doctype html><html><body>" + html);
        var body = document.Body!;
        if (DepthOf(body) > MaxDepth)
        {
            throw Rejected($"The content is nested more than {MaxDepth} levels deep.");
        }

        DropDangerousNodes(body);
        sanitizer.SanitizeDom(document, body, string.Empty);

        using var writer = new StringWriter();
        foreach (var node in body.ChildNodes)
        {
            node.ToHtml(writer, sanitizer.OutputFormatter);
        }

        return new SanitizedHtml(writer.ToString(), !string.IsNullOrWhiteSpace(body.TextContent));
    }

    // Before parsing: every '<' followed by a letter opens an element (or is text the parser turns into one). An upper bound, cheap.
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

    // Iterative on purpose: this is the guard against the recursion.
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

    private static ValidationException Rejected(string message) =>
        new([new ValidationFailure("ContentHtml", message)]);

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
