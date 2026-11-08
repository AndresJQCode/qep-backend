using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Confirmación de que la cuenta de comprador se eliminó. No lleva enlace.</summary>
public static class BuyerAccountDeletedEmailTemplate
{
    public const string TemplateRef = "buyers.account-deleted.v1";

    /// <summary>El prefijo que comparten las plantillas de correo del comprador. Con él se encuentran las
    /// notificaciones que se anonimizan cuando la cuenta se elimina.</summary>
    public const string BuyerTemplateRefPrefix = "buyers.";

    public static EmailMessage Render(string recipientAddress, string storeName)
    {
        const string subject = "Eliminamos tu cuenta";
        var htmlStore = WebUtility.HtmlEncode(storeName);

        var textBody =
            "Hola,\n\n" +
            $"Eliminaste tu cuenta desde {storeName}.\n" +
            "Borramos tu cuenta, tus datos guardados y tus sesiones. Los pedidos que ya hiciste en cada tienda no cambian.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            $"<p>Eliminaste tu cuenta desde {htmlStore}.</p>" +
            "<p>Borramos tu cuenta, tus datos guardados y tus sesiones. Los pedidos que ya hiciste en cada tienda no cambian.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
