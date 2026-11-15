using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Lo que necesita el correo de respuesta de un ticket: destinatario, el nombre del tenant que responde, el radicado
/// (<c>SOP-000127</c>), el asunto del ticket y el texto del asesor.</summary>
public sealed record SupportReplyEmailInput(string ToAddress, string TenantName, string TicketNumber, string TicketSubject, string Text);

/// <summary>
/// Soporte fase 2 (plan E15): la respuesta del asesor a un ticket por correo. El texto lo escribe una persona, así que en el cuerpo
/// HTML va escapado y con sus saltos de línea como <c>&lt;br&gt;</c>; en el de texto plano va tal cual. Tutea, como todo texto que el
/// producto le manda a una persona.
/// </summary>
public static class SupportReplyEmailTemplate
{
    public const string TemplateRef = "support.ticket-reply.v1";

    public static EmailMessage Render(SupportReplyEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var subject = $"Respuesta a tu caso {input.TicketNumber}";
        var text = Normalize(input.Text);

        var textBody =
            $"Hola,\n\n" +
            $"{input.TenantName} respondió tu caso {input.TicketNumber} ({input.TicketSubject}):\n\n" +
            $"{text}\n\n" +
            $"Si tienes dudas, responde a quien te atiende por el mismo medio por el que nos escribiste.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>{Html(input.TenantName)} respondió tu caso <strong>{Html(input.TicketNumber)}</strong> " +
            $"({Html(input.TicketSubject)}):</p>" +
            $"<p>{Html(text).Replace("\n", "<br>", StringComparison.Ordinal)}</p>" +
            $"<p>Si tienes dudas, responde a quien te atiende por el mismo medio por el que nos escribiste.</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }

    // Un solo tipo de salto: así el HTML no deja un \r suelto antes de cada <br>.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
