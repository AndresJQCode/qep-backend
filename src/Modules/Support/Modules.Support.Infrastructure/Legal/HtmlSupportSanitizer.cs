using BuildingBlocks.Infrastructure.RichText;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Legal;

/// <summary>
/// <see cref="ISupportHtmlSanitizer"/> sobre el saneador compartido (<see cref="RichTextHtmlSanitizer"/>, spec 2026-11-16 §5.2), con
/// la allowlist de spec 2026-11-15 §6.2 (plan L2 y L3). El núcleo —allowlist, pasadas hasta punto fijo, límites y <c>rel</c>/<c>target</c>
/// de los enlaces— vive en BuildingBlocks; acá sólo se fija la clave del rechazo (<c>ContentHtml</c>) y se mapea a <see cref="SanitizedHtml"/>.
/// Sin estado: se registra como singleton.
/// </summary>
internal sealed class HtmlSupportSanitizer : ISupportHtmlSanitizer
{
    private const string PropertyName = "ContentHtml";

    internal const int MaxDepth = RichTextHtmlSanitizer.MaxDepth;
    internal const int MaxElements = RichTextHtmlSanitizer.MaxElements;

    private static readonly RichTextHtmlSanitizer Core = new();

    public SanitizedHtml Sanitize(string html)
    {
        var sanitized = Core.Sanitize(html, PropertyName);
        return new SanitizedHtml(sanitized.Html, sanitized.HasVisibleText);
    }
}
