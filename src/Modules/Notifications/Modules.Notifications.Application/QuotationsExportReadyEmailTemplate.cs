using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// El correo de exportación lista de cotizaciones o pedidos. Mismo criterio que
/// <see cref="CustomerExportEmailTemplate"/>: plantilla fija, variables en allowlist y el
/// vencimiento dicho explícito, porque un enlace que caduca sin aviso se lee como una falla.
/// </summary>
public static class QuotationsExportReadyEmailTemplate
{
    public const string TemplateRef = "quotations.export-ready.v1";

    public static EmailMessage Render(
        string recipientAddress,
        string kind,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt,
        EmailRegionalFormat format)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"Tu exportación de {names.Plural} está lista";
        // En la hora del tenant del export y sin etiqueta de huso (spec 2026-09-17, punto 8b): es la
        // hora que la persona ve en su reloj. El patrón de la fecha es el del tenant (spec 2026-10-23,
        // D-R2) y se escribe con InvariantCulture: la `/` es el separador de la cultura, no un literal.
        var expiry = format.Expiry(expiresAt);
        var rows = rowCount == 1
            ? $"1 {names.Singular}"
            : $"{format.Count(rowCount)} {names.Plural}";

        // HTML con la URL escapada y texto plano con la URL cruda: mismo motivo que en
        // CustomerExportEmailTemplate (la firma de R2 se rompe con un `&` mal normalizado).
        var htmlUrl = WebUtility.HtmlEncode(downloadUrl);
        var htmlFileName = WebUtility.HtmlEncode(fileName);

        var textBody =
            $"Hola,\n\n" +
            $"La exportación que solicitaste ya está lista: {fileName} ({rows}).\n" +
            $"Descárgala desde este enlace:\n\n" +
            $"{downloadUrl}\n\n" +
            $"El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>La exportación que solicitaste ya está lista: " +
            $"<strong>{htmlFileName}</strong> ({rows}).</p>" +
            $"<p><a href=\"{htmlUrl}\">Descargar exportación</a></p>" +
            $"<p>El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
