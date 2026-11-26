using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// El correo con el que un mayorista invita a un distribuidor (spec 2026-11-17, mayorista fase 2). Mismo criterio que
/// <see cref="InvitationEmailTemplate"/>: plantilla inmutable, variables en allowlist (destinatario, enlace, nombre del
/// mayorista, razón social del distribuidor y vencimiento) y español. La copia es la propuesta del plan (O-3); el owner la puede
/// ajustar subiendo la referencia.
///
/// El vencimiento se dice porque el enlace caduca a las 72 horas: sin esa frase, un enlace vencido se lee como una falla.
/// </summary>
public static class WholesaleInvitationEmailTemplate
{
    public const string TemplateRef = "wholesale-invitation";

    public static EmailMessage Render(
        string recipient,
        string link,
        string wholesalerName,
        string businessName,
        DateTimeOffset expiresAt,
        EmailRegionalFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        var subject = $"{wholesalerName} te invita a comprarle en Lulo CRM";
        // En la hora del tenant del mayorista y con su patrón de fecha (spec 2026-10-23, D-R2).
        var expiry = format.Expiry(expiresAt);

        // Los dos nombres los escribe un usuario (el del tenant y la razón social del formulario): en el HTML van escapados, en
        // el texto plano crudos. El enlace es nuestro (base configurada + token base64url escapado) y no lleva `&`.
        var htmlWholesaler = WebUtility.HtmlEncode(wholesalerName);
        var htmlBusiness = WebUtility.HtmlEncode(businessName);
        var htmlLink = WebUtility.HtmlEncode(link);

        var textBody =
            $"Hola.\n\n" +
            $"{wholesalerName} quiere venderle a {businessName} por Lulo CRM. " +
            $"Acepta la invitación para crear tus órdenes de compra con sus precios.\n\n" +
            $"{link}\n\n" +
            $"El enlace vence el {expiry}.\n";

        var htmlBody =
            $"<p>Hola.</p>" +
            $"<p>{htmlWholesaler} quiere venderle a {htmlBusiness} por Lulo CRM. " +
            $"Acepta la invitación para crear tus órdenes de compra con sus precios.</p>" +
            $"<p><a href=\"{htmlLink}\">Aceptar invitación</a></p>" +
            $"<p>El enlace vence el {expiry}.</p>";

        return new EmailMessage(recipient, subject, htmlBody, textBody);
    }
}
