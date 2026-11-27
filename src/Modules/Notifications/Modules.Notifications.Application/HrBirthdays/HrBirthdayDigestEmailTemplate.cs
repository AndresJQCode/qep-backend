using System.Net;
using System.Text;

namespace Modules.Notifications.Application;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: the email of the daily birthday digest. Subject «Cumpleaños de hoy en tu equipo», or «Próximos
/// cumpleaños en tu equipo» when only upcoming ones are announced. No company name (it would need another lookup and the reader already knows
/// their company). Each employee links to their profile; the names come from people, so the HTML encodes them. Tutea, like every text the product
/// sends. Names only: never a birth year or an age.
/// </summary>
public static class HrBirthdayDigestEmailTemplate
{
    public const string TemplateRef = "hr.birthdays-digest.v1";

    /// <param name="today">The people shown (at most what Hr capped); <paramref name="todayTotal"/> is the real count, for «y N más».</param>
    /// <param name="upcomingDate">The date of the upcoming birthdays; <c>null</c> when the setting has no lead.</param>
    public static EmailMessage Render(
        string to,
        IReadOnlyList<(Guid Id, string Name)> today,
        int todayTotal,
        IReadOnlyList<(Guid Id, string Name)> upcoming,
        int upcomingTotal,
        DateOnly? upcomingDate,
        string appBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(upcoming);
        var subject = today.Count > 0 ? "Cumpleaños de hoy en tu equipo" : "Próximos cumpleaños en tu equipo";

        var text = new StringBuilder("Hola,\n");
        var html = new StringBuilder("<p>Hola,</p>");
        if (today.Count > 0)
        {
            AppendGroup(text, html, "Hoy cumplen años en tu equipo:", today, todayTotal, appBaseUrl);
        }

        if (upcoming.Count > 0)
        {
            var heading = upcomingDate is { } date
                ? $"Próximos cumpleaños ({HrBirthdayWhatsAppText.Date(date)}):"
                : "Próximos cumpleaños:";
            AppendGroup(text, html, heading, upcoming, upcomingTotal, appBaseUrl);
        }

        text.Append("\nPuedes ver el detalle en la campana de QEP.\n");
        html.Append("<p>Puedes ver el detalle en la campana de QEP.</p>");
        return new EmailMessage(to, subject, html.ToString(), text.ToString());
    }

    private static void AppendGroup(
        StringBuilder text, StringBuilder html, string heading, IReadOnlyList<(Guid Id, string Name)> people, int total, string appBaseUrl)
    {
        text.Append('\n').Append(heading).Append('\n');
        html.Append("<p>").Append(Html(heading)).Append("</p><ul>");
        foreach (var (id, name) in people)
        {
            var link = AccountLinks.HrEmployee(appBaseUrl, id);
            text.Append("- ").Append(name).Append(": ").Append(link).Append('\n');
            html.Append("<li><a href=\"").Append(Html(link)).Append("\">").Append(Html(name)).Append("</a></li>");
        }

        html.Append("</ul>");
        if (total > people.Count)
        {
            var more = $"y {total - people.Count} más";
            text.Append(more).Append('\n');
            html.Append("<p>").Append(more).Append("</p>");
        }
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
