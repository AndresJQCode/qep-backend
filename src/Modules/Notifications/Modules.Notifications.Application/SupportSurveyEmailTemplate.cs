using System.Net;

namespace Modules.Notifications.Application;

/// <summary>Lo que necesita el correo de la encuesta de un ticket: destinatario, el nombre del tenant, el radicado (<c>SOP-000127</c>), el
/// asunto del ticket y el enlace de calificación (<c>{BaseUrl}/{slug}/calificar?token=…</c>, H19). El enlace lleva el token: nunca va a un
/// log.</summary>
public sealed record SupportSurveyEmailInput(string ToAddress, string TenantName, string TicketNumber, string TicketSubject, Uri RatingUrl);

/// <summary>
/// Soporte fase 4b (H16): la encuesta de satisfacción por correo, con el enlace a la página pública de calificación. El enlace va como
/// <see cref="Uri.AbsoluteUri"/> (no <c>ToString()</c>, que desescapa la query) y escapado en el HTML, así un <c>&amp;</c> de la query no
/// rompe el atributo. El asunto del ticket lo escribió el solicitante: va escapado. Tutea, como todo texto que el producto le manda a una
/// persona.
/// </summary>
public static class SupportSurveyEmailTemplate
{
    public const string TemplateRef = "support.ticket-survey.v1";

    public static EmailMessage Render(SupportSurveyEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.RatingUrl);
        var subject = $"¿Cómo te atendimos en tu caso {input.TicketNumber}?";
        var url = input.RatingUrl.AbsoluteUri;

        var textBody =
            $"Hola,\n\n" +
            $"Tu caso {input.TicketNumber} ({input.TicketSubject}) con {input.TenantName} quedó resuelto.\n\n" +
            $"Califica la atención que recibiste en este enlace:\n\n" +
            $"{url}\n\n" +
            $"Te toma menos de un minuto y nos ayuda a mejorar. ¡Gracias!\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>Tu caso <strong>{Html(input.TicketNumber)}</strong> ({Html(input.TicketSubject)}) con {Html(input.TenantName)} " +
            $"{Html("quedó resuelto.")}</p>" +
            $"<p><a href=\"{Html(url)}\">{Html("Califica la atención que recibiste")}</a></p>" +
            $"<p>{Html("Si el enlace no abre, copia esta dirección en tu navegador:")} {Html(url)}</p>" +
            $"<p>{Html("Te toma menos de un minuto y nos ayuda a mejorar. ¡Gracias!")}</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
