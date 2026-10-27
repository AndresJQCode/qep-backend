namespace Modules.Notifications.Application;

/// <summary>Aviso para quien intentó registrarse con un correo que ya tiene cuenta. No lleva token y no
/// nombra ningún tenant: sólo apunta a iniciar sesión o a recuperar la contraseña.</summary>
public static class ExistingAccountEmailTemplate
{
    public const string TemplateRef = "identity.existing-account.v1";

    public static EmailMessage Render(string recipientAddress, string loginUrl, string forgotPasswordUrl)
    {
        const string subject = "Ya tienes una cuenta";

        var textBody =
            "Hola,\n\n" +
            "Alguien intentó crear una cuenta con este correo, pero ya tienes una.\n\n" +
            $"Inicia sesión aquí:\n{loginUrl}\n\n" +
            $"Si olvidaste tu contraseña, puedes restablecerla aquí:\n{forgotPasswordUrl}\n\n" +
            "Si no fuiste tú, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            "<p>Alguien intentó crear una cuenta con este correo, pero ya tienes una.</p>" +
            $"<p><a href=\"{loginUrl}\">Iniciar sesión</a></p>" +
            $"<p>Si olvidaste tu contraseña, <a href=\"{forgotPasswordUrl}\">puedes restablecerla aquí</a>.</p>" +
            "<p>Si no fuiste tú, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
