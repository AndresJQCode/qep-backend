namespace Modules.Notifications.Application;

/// <summary>
/// Renderiza el email de invitación al tenant. Las plantillas publicadas son inmutables y
/// usan un set de variables en allowlist (destinatario, tenant, url de login); la
/// localización es español para la v1. La referencia de plantilla es estable para auditar.
/// </summary>
public static class InvitationEmailTemplate
{
    public const string TemplateRef = "identity.invitation.v1";

    public static EmailMessage Render(string recipientAddress, Guid tenantId, string loginUrl)
    {
        const string subject = "Te invitaron a QCode Enterprise Platform";

        var textBody =
            $"Hola,\n\n" +
            $"Has sido invitado a una organización en QCode Enterprise Platform.\n" +
            $"Inicia sesión con tu cuenta de Google para aceptar la invitación:\n\n" +
            $"{loginUrl}\n\n" +
            $"Si no esperabas esta invitación, puedes ignorar este mensaje.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>Has sido invitado a una organización en <strong>QCode Enterprise Platform</strong>.</p>" +
            $"<p>Inicia sesión con tu cuenta de Google para aceptar la invitación:</p>" +
            $"<p><a href=\"{loginUrl}\">Aceptar invitación</a></p>" +
            $"<p>Si no esperabas esta invitación, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
