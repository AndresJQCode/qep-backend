namespace Modules.Notifications.Application;

/// <summary>Correo para confirmar la dirección de una cuenta con contraseña. No nombra ningún tenant:
/// la cuenta existe antes que cualquier empresa. El enlace vence en 24 horas.</summary>
public static class EmailVerificationEmailTemplate
{
    public const string TemplateRef = "identity.email-verification.v1";

    public static EmailMessage Render(string recipientAddress, string verifyUrl)
    {
        const string subject = "Confirma tu correo";

        var textBody =
            "Hola,\n\n" +
            "Para terminar de crear tu cuenta, confirma tu correo con este enlace:\n\n" +
            $"{verifyUrl}\n\n" +
            "El enlace vence en 24 horas.\n" +
            "Si no creaste una cuenta, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            "<p>Para terminar de crear tu cuenta, confirma tu correo con este enlace:</p>" +
            $"<p><a href=\"{verifyUrl}\">Confirmar mi correo</a></p>" +
            "<p>El enlace vence en 24 horas.</p>" +
            "<p>Si no creaste una cuenta, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
