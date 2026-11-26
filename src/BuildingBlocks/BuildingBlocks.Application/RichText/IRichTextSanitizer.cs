namespace BuildingBlocks.Application.RichText;

/// <summary>
/// HTML de texto enriquecido ya saneado: <see cref="Html"/> es un punto fijo (sanearlo otra vez da los mismos bytes) y
/// <see cref="HasVisibleText"/> dice si queda texto que se vea, para rechazar un documento que sólo trae etiquetas vacías.
/// </summary>
public sealed record RichTextSanitized(string Html, bool HasVisibleText);

/// <summary>
/// La allowlist del texto enriquecido que escribe un tenant (spec 2026-11-15 §6.2, compartida por el spec 2026-11-16 §5.2): párrafos,
/// títulos h2–h4, marcas, listas, citas, separador y enlaces https, mailto o tel. La implementación vive en
/// <c>BuildingBlocks.Infrastructure</c>, sobre <c>HtmlSanitizer</c>: Application no conoce la librería.
/// <para>
/// Un HTML demasiado grande, demasiado anidado o que no se estabiliza lanza <c>FluentValidation.ValidationException</c> con la
/// clave <paramref name="propertyName"/> (<c>422 validation.failed</c>, con el mapa <c>errors</c> que el formulario sabe leer).
/// </para>
/// </summary>
public interface IRichTextSanitizer
{
    RichTextSanitized Sanitize(string html, string propertyName);
}
