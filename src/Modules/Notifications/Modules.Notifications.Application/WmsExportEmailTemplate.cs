using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el correo con el enlace de descarga del Excel de saldos de bodega (spec WMS 2026-10-31, S15). Calcado de
/// <see cref="SupplierExportEmailTemplate"/> y con plantilla propia: reutilizar la de proveedores mandaría este correo con su asunto y
/// su texto.
///
/// El vencimiento se dice explícitamente porque el enlace caduca: sin esa frase, un enlace que deja de funcionar se lee como una
/// falla del sistema en vez de como lo que es.
/// </summary>
public static class WmsExportEmailTemplate
{
    public const string TemplateRef = "wms.export-ready.v1";

    public static EmailMessage Render(
        string recipientAddress,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt,
        EmailRegionalFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        const string subject = "Tu exportación de saldos está lista";

        // En la hora del tenant y sin etiqueta de huso: es la que la persona ve en su reloj. El patrón de la fecha y el separador del
        // conteo son los del tenant.
        var expiry = format.Expiry(expiresAt);
        var rows = rowCount == 1 ? "1 fila" : $"{format.Count(rowCount)} filas";

        // El HTML lleva la URL escapada y el texto plano la cruda: una URL prefirmada separa sus parámetros con `&`, y un `&` sin
        // declarar dentro de un `href` deja el enlace a merced de cómo cada cliente de correo normalice el HTML. En texto plano no
        // hay entidades que declarar: escaparla ahí la rompe.
        var htmlUrl = WebUtility.HtmlEncode(downloadUrl);
        var htmlFileName = WebUtility.HtmlEncode(fileName);

        var textBody =
            $"Hola,\n\n" +
            $"La exportación de saldos que solicitaste ya está lista: {fileName} ({rows}).\n" +
            $"Descárgala desde este enlace:\n\n" +
            $"{downloadUrl}\n\n" +
            $"El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>La exportación de saldos que solicitaste ya está lista: " +
            $"<strong>{htmlFileName}</strong> ({rows}).</p>" +
            $"<p><a href=\"{htmlUrl}\">Descargar exportación</a></p>" +
            $"<p>El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
