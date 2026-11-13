namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-13: la URL de un encabezado multimedia que manda el integrador. QEP nunca la descarga: la entrega a Meta tal cual.
/// Una sola definición de «URL válida» para la entrada (el validador del envío) y para la salida (<see cref="TemplateDetailsJson.ParseRef"/>).</summary>
public static class ExternalMediaLink
{
    public const int MaxLinkLength = 2048;

    /// <summary>https absoluto con host, de 1 a <see cref="MaxLinkLength"/> caracteres, sin espacios ni caracteres de control.</summary>
    public static bool IsValidLink(string? link) =>
        link is { Length: > 0 and <= MaxLinkLength }
        && !link.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
        && Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0;
}
