namespace Modules.Notifications.Application;

/// <summary>El aviso suave de un cobro por vencer (spec cobros-recurrentes §3.3), uno por cliente y día.</summary>
public static class BillingReminderEmailTemplate
{
    public const string TemplateRef = "billing.reminder.v1";

    public static EmailMessage Render(BillingCustomerEmailInput input, EmailRegionalFormat format)
    {
        var due = BillingEmailParts.Date(input.NearestDueDate, format);
        var subject = input.Lines.Count == 1
            ? $"Tu servicio {input.Lines[0].ServiceName} vence el {due}"
            : $"Tus servicios vencen el {due}";
        var total = BillingEmailParts.Totals(input.TotalByCurrency, format);
        const string closing = "Si ya realizaste el pago, ignora este mensaje.";

        var textBody =
            $"Hola {input.CustomerName},\n\n" +
            $"Te recordamos de parte de {input.TenantName} los servicios que están por vencer:\n\n" +
            string.Concat(input.Lines.Select(line =>
                $"- {line.ServiceName}: {BillingEmailParts.Money(line.Amount, line.Currency, format)}, vence el {BillingEmailParts.Date(line.DueDate, format)}\n")) +
            $"\nTotal: {total}\n\n{closing}\n";

        var table = BillingEmailParts.HtmlTable(
            ["Servicio", "Monto", "Vence"],
            input.Lines.Select(line => new[]
            {
                line.ServiceName,
                BillingEmailParts.Money(line.Amount, line.Currency, format),
                BillingEmailParts.Date(line.DueDate, format),
            }));

        var htmlBody =
            $"<p>Hola {BillingEmailParts.Html(input.CustomerName)},</p>" +
            $"<p>Te recordamos de parte de <strong>{BillingEmailParts.Html(input.TenantName)}</strong> los servicios que están por vencer:</p>" +
            table +
            $"<p>Total: {total}</p>" +
            $"<p>{closing}</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }
}
