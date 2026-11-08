using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Correo para confirmar la cuenta de comprador de una tienda. El enlace vence en 24 horas. El
/// nombre de la tienda va codificado en el HTML; el asunto y el texto plano lo llevan tal cual.</summary>
public static class BuyerVerificationEmailTemplate
{
    public const string TemplateRef = "buyers.verification.v1";

    public static EmailMessage Render(string recipientAddress, string storeName, string verifyUrl)
    {
        var subject = $"Confirma tu cuenta para comprar en {storeName}";
        var htmlStore = WebUtility.HtmlEncode(storeName);

        var textBody =
            "Hola,\n\n" +
            $"Para terminar de crear tu cuenta en {storeName}, confirma tu correo con este enlace:\n\n" +
            $"{verifyUrl}\n\n" +
            "El enlace vence en 24 horas.\n" +
            "Si no creaste una cuenta, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            $"<p>Para terminar de crear tu cuenta en {htmlStore}, confirma tu correo con este enlace:</p>" +
            $"<p><a href=\"{verifyUrl}\">Confirmar mi correo</a></p>" +
            "<p>El enlace vence en 24 horas.</p>" +
            "<p>Si no creaste una cuenta, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
