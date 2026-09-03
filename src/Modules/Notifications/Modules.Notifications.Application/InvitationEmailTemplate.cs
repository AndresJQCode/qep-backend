namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el email de invitación al tenant. Las plantillas publicadas son inmutables y
/// usan un set de variables en allowlist (destinatario, url de invitación); la
/// localización es español para la v1 del producto. La referencia de plantilla es estable
/// para auditar: la v2 reemplaza el link genérico de login por el deep-link con el token
/// de invitación, así que la referencia sube de versión en vez de reusar la v1.
/// </summary>
public static class InvitationEmailTemplate
{
    public const string TemplateRef = "identity.invitation.v2";

    public static EmailMessage Render(string recipientAddress, string invitationUrl)
    {
        const string subject = "Te invitaron a Origen Botánico";

        var textBody =
            $"Hola,\n\n" +
            $"Has sido invitado a una organización en Origen Botánico.\n" +
            $"Abre este enlace e inicia sesión con tu cuenta de Google para aceptar la invitación:\n\n" +
            $"{invitationUrl}\n\n" +
            $"Si no esperabas esta invitación, puedes ignorar este mensaje.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>Has sido invitado a una organización en <strong>Origen Botánico</strong>.</p>" +
            $"<p>Abre este enlace e inicia sesión con tu cuenta de Google para aceptar la invitación:</p>" +
            $"<p><a href=\"{invitationUrl}\">Aceptar invitación</a></p>" +
            $"<p>Si no esperabas esta invitación, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
