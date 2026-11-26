using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Task-notices spec 2026-11-22 §5.6 and D-7: the email that takes over when an assignment was not read in time and WhatsApp is not an
/// option (or failed). It may carry the subject, unlike the WhatsApp template, which never does. The subject and the actor come from
/// people, so the HTML body encodes them. Tutea, like every text the product sends.
/// </summary>
public static class SupportTaskAssignedEmailTemplate
{
    public const string TemplateRef = "support.task-assigned.v1";

    /// <param name="isTask">An internal task («la tarea») or a customer ticket («el ticket»).</param>
    /// <param name="actorName">Who assigned it; without a name the sentence is impersonal.</param>
    public static EmailMessage Render(
        string recipientAddress, bool isTask, string? actorName, string ticketNumber, string subject, string link)
    {
        var noun = isTask ? "la tarea" : "el ticket";
        var open = isTask ? "Ábrela" : "Ábrelo";
        var hasActor = !string.IsNullOrWhiteSpace(actorName);
        var emailSubject = $"Te asignaron {noun} {ticketNumber}";

        var lead = hasActor ? $"{actorName} te asignó {noun} {ticketNumber}" : $"Te asignaron {noun} {ticketNumber}";
        var leadHtml = hasActor
            ? $"{Html(actorName!)} te asignó {noun} <strong>{Html(ticketNumber)}</strong>"
            : $"Te asignaron {noun} <strong>{Html(ticketNumber)}</strong>";

        var textBody = $"Hola,\n\n{lead}: «{subject}». {open} en QEP: {link}\n";
        var htmlBody =
            "<p>Hola,</p>" +
            $"<p>{leadHtml}: «{Html(subject)}».</p>" +
            $"<p>{open} en QEP: <a href=\"{Html(link)}\">{Html(link)}</a></p>";

        return new EmailMessage(recipientAddress, emailSubject, htmlBody, textBody);
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
