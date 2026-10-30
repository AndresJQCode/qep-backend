using System.Globalization;
using System.Text;

namespace Modules.Notifications.Application;

/// <summary>
/// El resumen diario al encargado (spec cobros-recurrentes §3.3, paso 5): por vencer, vencidos y avisos fallidos. Una sección
/// vacía no se dibuja.
/// </summary>
public static class BillingDigestEmailTemplate
{
    public const string TemplateRef = "billing.digest.v1";

    public static EmailMessage Render(BillingDigestEmailInput input, EmailRegionalFormat format)
    {
        var subject =
            $"Cobros de hoy: {input.DueSoon.Count} por vencer, {input.Overdue.Count} vencidos, {input.FailedReminders.Count} avisos fallidos";

        var text = new StringBuilder($"Hola {input.MemberName},\n\nEste es el resumen de cobros de {input.TenantName}.\n");
        var html = new StringBuilder(
            $"<p>Hola {BillingEmailParts.Html(input.MemberName)},</p>" +
            $"<p>Este es el resumen de cobros de <strong>{BillingEmailParts.Html(input.TenantName)}</strong>.</p>");

        Section(text, html, "Por vencer", input.DueSoon, format, withFailure: false);
        Section(text, html, "Vencidos", input.Overdue, format, withFailure: false);
        if (input.Overdue.Count > 0 && input.OverdueTotalByCurrency.Count > 0)
        {
            var total = BillingEmailParts.Totals(input.OverdueTotalByCurrency);
            text.Append(CultureInfo.InvariantCulture, $"\nTotal vencido: {total}\n");
            html.Append(CultureInfo.InvariantCulture, $"<p>Total vencido: {total}</p>");
        }

        Section(text, html, "Avisos fallidos", input.FailedReminders, format, withFailure: true);

        return new EmailMessage(input.ToAddress, subject, html.ToString(), text.ToString());
    }

    private static void Section(
        StringBuilder text,
        StringBuilder html,
        string title,
        IReadOnlyList<BillingDigestEmailRow> rows,
        EmailRegionalFormat format,
        bool withFailure)
    {
        if (rows.Count == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n{title}\n");
        foreach (var row in rows)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {row.CustomerName} / {row.ServiceName}: {row.Amount}, {BillingEmailParts.Date(row.DueDate, format)}");
            text.Append(withFailure && row.FailureCode is not null ? $" ({row.FailureCode})\n" : "\n");
        }

        string[] headers = withFailure
            ? ["Cliente", "Servicio", "Monto", "Vence", "Motivo"]
            : ["Cliente", "Servicio", "Monto", "Vence"];
        html.Append(CultureInfo.InvariantCulture, $"<h3>{title}</h3>");
        html.Append(BillingEmailParts.HtmlTable(
            headers,
            rows.Select(row =>
            {
                var cells = new List<string>
                {
                    row.CustomerName,
                    row.ServiceName,
                    row.Amount,
                    BillingEmailParts.Date(row.DueDate, format),
                };
                if (withFailure)
                {
                    cells.Add(row.FailureCode ?? string.Empty);
                }

                return (IEnumerable<string>)cells;
            })));
    }
}
