using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el email de invitación al tenant. Las plantillas publicadas son inmutables y
/// usan un set de variables en allowlist (destinatario, url de invitación, nombre del tenant);
/// la localización es español para la v1 del producto. La referencia de plantilla es estable
/// para auditar: la v2 reemplazó el link genérico de login por el deep-link con el token de
/// invitación y la v3 nombra al tenant en el asunto y en el cuerpo, así que la referencia sube
/// de versión en vez de reusar la anterior.
/// </summary>
public static class InvitationEmailTemplate
{
    public const string TemplateRef = "identity.invitation.v3";

    public static EmailMessage Render(string recipientAddress, string invitationUrl, string tenantName)
    {
        var subject = $"Te invitaron a {tenantName}";

        // El nombre del tenant lo escribe quien lo registra: es texto de usuario, y en el cuerpo
        // HTML va escapado. En texto plano no hay entidades que declarar y va crudo, mismo criterio
        // que la URL en <see cref="CustomerExportEmailTemplate"/>.
        var htmlTenantName = WebUtility.HtmlEncode(tenantName);

        var textBody =
            $"Hola,\n\n" +
            $"Has sido invitado a {tenantName}.\n" +
            $"Abre este enlace e inicia sesión con tu cuenta de Google para aceptar la invitación:\n\n" +
            $"{invitationUrl}\n\n" +
            $"Si no esperabas esta invitación, puedes ignorar este mensaje.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>Has sido invitado a <strong>{htmlTenantName}</strong>.</p>" +
            $"<p>Abre este enlace e inicia sesión con tu cuenta de Google para aceptar la invitación:</p>" +
            $"<p><a href=\"{invitationUrl}\">Aceptar invitación</a></p>" +
            $"<p>Si no esperabas esta invitación, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
