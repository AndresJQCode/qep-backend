namespace Modules.Notifications.Application;

/// <summary>Correo para restablecer la contraseña. No nombra ningún tenant. El enlace vence en 1 hora.</summary>
public static class PasswordResetEmailTemplate
{
    public const string TemplateRef = "identity.password-reset.v1";

    public static EmailMessage Render(string recipientAddress, string resetUrl)
    {
        const string subject = "Restablece tu contraseña";

        var textBody =
            "Hola,\n\n" +
            "Recibimos una solicitud para restablecer tu contraseña. Usa este enlace para elegir una nueva:\n\n" +
            $"{resetUrl}\n\n" +
            "El enlace vence en 1 hora.\n" +
            "Si no lo solicitaste, puedes ignorar este mensaje: tu contraseña no cambiará.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            "<p>Recibimos una solicitud para restablecer tu contraseña. Usa este enlace para elegir una nueva:</p>" +
            $"<p><a href=\"{resetUrl}\">Restablecer mi contraseña</a></p>" +
            "<p>El enlace vence en 1 hora.</p>" +
            "<p>Si no lo solicitaste, puedes ignorar este mensaje: tu contraseña no cambiará.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
