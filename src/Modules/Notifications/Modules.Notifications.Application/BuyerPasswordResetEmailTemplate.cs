using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Correo para restablecer la contraseña de una cuenta de comprador. Nombra la tienda en el
/// cuerpo; el enlace vence en 1 hora.</summary>
public static class BuyerPasswordResetEmailTemplate
{
    public const string TemplateRef = "buyers.password-reset.v1";

    public static EmailMessage Render(string recipientAddress, string storeName, string resetUrl)
    {
        const string subject = "Restablece tu contraseña";
        var htmlStore = WebUtility.HtmlEncode(storeName);

        var textBody =
            "Hola,\n\n" +
            $"Pediste restablecer la contraseña de tu cuenta para comprar en {storeName}. Hazlo con este enlace:\n\n" +
            $"{resetUrl}\n\n" +
            "El enlace vence en 1 hora.\n" +
            "Si no lo pediste, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            $"<p>Pediste restablecer la contraseña de tu cuenta para comprar en {htmlStore}. Hazlo con este enlace:</p>" +
            $"<p><a href=\"{resetUrl}\">Restablecer mi contraseña</a></p>" +
            "<p>El enlace vence en 1 hora.</p>" +
            "<p>Si no lo pediste, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
