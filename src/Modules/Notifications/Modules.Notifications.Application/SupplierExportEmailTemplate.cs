using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el email con el enlace de descarga de una exportación de proveedores. Mismo criterio que
/// <see cref="InvitationEmailTemplate"/>: plantilla inmutable, variables en allowlist (destinatario,
/// enlace, nombre de archivo, cantidad y vencimiento) y localización en español para la v1.
///
/// El vencimiento se dice explícitamente porque el enlace caduca: sin esa frase, un enlace que deja
/// de funcionar se lee como una falla del sistema en vez de como lo que es.
/// </summary>
public static class SupplierExportEmailTemplate
{
    public const string TemplateRef = "suppliers.export-ready.v1";

    public static EmailMessage Render(
        string recipientAddress,
        string downloadUrl,
        string fileName,
        int supplierCount,
        DateTimeOffset expiresAt,
        EmailRegionalFormat format)
    {
        const string subject = "Tu exportación de proveedores está lista";

        // En la hora del tenant del export y sin etiqueta de huso (spec 2026-09-17, punto 8b): es la
        // hora que la persona ve en su reloj. El patrón de la fecha es el del tenant (spec 2026-10-23,
        // D-R2) y se escribe con InvariantCulture: la `/` es el separador de la cultura, no un literal.
        var expiry = format.Expiry(expiresAt);
        var suppliers = supplierCount == 1 ? "1 proveedor" : $"{format.Count(supplierCount)} proveedores";

        // El cuerpo HTML lleva la URL **escapada**; el de texto plano, cruda. No es simetria
        // rota: una URL prefirmada separa sus parametros `X-Amz-*` con `&`, y un `&` sin
        // declarar dentro de un `href` deja el enlace a merced de como cada cliente de correo
        // normalice el HTML. Si termina partido o con `&amp;` literal, R2 contesta
        // `400 InvalidArgument / Authorization` y el usuario ve un enlace roto sin pista de por
        // que. En texto plano no hay entidades que declarar: escaparla ahi la rompe.
        var htmlUrl = WebUtility.HtmlEncode(downloadUrl);
        var htmlFileName = WebUtility.HtmlEncode(fileName);

        var textBody =
            $"Hola,\n\n" +
            $"La exportación que solicitaste ya está lista: {fileName} ({suppliers}).\n" +
            $"Descárgala desde este enlace:\n\n" +
            $"{downloadUrl}\n\n" +
            $"El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>La exportación que solicitaste ya está lista: " +
            $"<strong>{htmlFileName}</strong> ({suppliers}).</p>" +
            $"<p><a href=\"{htmlUrl}\">Descargar exportación</a></p>" +
            $"<p>El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
