using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Aviso para quien intentó registrarse en una tienda con un correo que ya tiene cuenta de
/// comprador. No lleva token: sólo enlaces a ingresar y a recuperar la contraseña.</summary>
public static class BuyerExistingAccountEmailTemplate
{
    public const string TemplateRef = "buyers.existing-account.v1";

    public static EmailMessage Render(string recipientAddress, string storeName, string loginUrl, string forgotUrl)
    {
        var subject = $"Ya tienes una cuenta para comprar en {storeName}";
        var htmlStore = WebUtility.HtmlEncode(storeName);

        var textBody =
            "Hola,\n\n" +
            $"Alguien intentó crear una cuenta en {storeName} con este correo, pero ya tienes una.\n" +
            "La próxima vez inicia sesión con tu correo y tu contraseña.\n\n" +
            $"Inicia sesión aquí:\n{loginUrl}\n\n" +
            $"Si olvidaste tu contraseña, puedes restablecerla aquí:\n{forgotUrl}\n\n" +
            "Si no fuiste tú, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            $"<p>Alguien intentó crear una cuenta en {htmlStore} con este correo, pero ya tienes una.</p>" +
            "<p>La próxima vez inicia sesión con tu correo y tu contraseña.</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(loginUrl)}\">Iniciar sesión</a></p>" +
            $"<p>Si olvidaste tu contraseña, <a href=\"{WebUtility.HtmlEncode(forgotUrl)}\">puedes restablecerla aquí</a>.</p>" +
            "<p>Si no fuiste tú, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
