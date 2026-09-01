using System.Globalization;
using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el email con el enlace de descarga de una exportación de clientes. Mismo criterio que
/// <see cref="InvitationEmailTemplate"/>: plantilla inmutable, variables en allowlist (destinatario,
/// enlace, nombre de archivo, cantidad y vencimiento) y localización en español para la v1.
///
/// El vencimiento se dice explícitamente porque el enlace caduca: sin esa frase, un enlace que deja
/// de funcionar se lee como una falla del sistema en vez de como lo que es.
/// </summary>
public static class CustomerExportEmailTemplate
{
    public const string TemplateRef = "customers.export-ready.v1";

    public static EmailMessage Render(
        string recipientAddress,
        string downloadUrl,
        string fileName,
        int customerCount,
        DateTimeOffset expiresAt)
    {
        const string subject = "Tu exportación de clientes está lista";

        var expiry = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var customers = customerCount == 1 ? "1 cliente" : $"{customerCount} clientes";

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
            $"La exportación que solicitaste ya está lista: {fileName} ({customers}).\n" +
            $"Descárgala desde este enlace:\n\n" +
            $"{downloadUrl}\n\n" +
            $"El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>La exportación que solicitaste ya está lista: " +
            $"<strong>{htmlFileName}</strong> ({customers}).</p>" +
            $"<p><a href=\"{htmlUrl}\">Descargar exportación</a></p>" +
            $"<p>El enlace vence el {expiry}. Después de esa fecha tendrás que solicitar la " +
            $"exportación de nuevo.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
