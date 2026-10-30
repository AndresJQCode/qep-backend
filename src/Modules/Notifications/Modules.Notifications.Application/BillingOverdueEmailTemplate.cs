namespace Modules.Notifications.Application;

/// <summary>El aviso firme de cobros vencidos (spec cobros-recurrentes §3.3): lleva los días de atraso de cada uno.</summary>
public static class BillingOverdueEmailTemplate
{
    public const string TemplateRef = "billing.overdue.v1";

    public static EmailMessage Render(BillingCustomerEmailInput input, EmailRegionalFormat format)
    {
        var subject = $"Tienes {input.Lines.Count} cobro(s) vencido(s)";
        var total = BillingEmailParts.Totals(input.TotalByCurrency, format);
        const string closing = "Escríbenos para ponerte al día.";

        var textBody =
            $"Hola {input.CustomerName},\n\n" +
            $"{input.TenantName} tiene registrados estos cobros vencidos a tu nombre:\n\n" +
            string.Concat(input.Lines.Select(line =>
                $"- {line.ServiceName}: {BillingEmailParts.Money(line.Amount, line.Currency, format)}, venció el {BillingEmailParts.Date(line.DueDate, format)} ({line.DaysOverdue} días de atraso)\n")) +
            $"\nTotal vencido: {total}\n\n{closing}\n";

        var table = BillingEmailParts.HtmlTable(
            ["Servicio", "Monto", "Venció", "Días de atraso"],
            input.Lines.Select(line => new[]
            {
                line.ServiceName,
                BillingEmailParts.Money(line.Amount, line.Currency, format),
                BillingEmailParts.Date(line.DueDate, format),
                line.DaysOverdue.ToString(format.Numbers),
            }));

        var htmlBody =
            $"<p>Hola {BillingEmailParts.Html(input.CustomerName)},</p>" +
            $"<p><strong>{BillingEmailParts.Html(input.TenantName)}</strong> tiene registrados estos cobros vencidos a tu nombre:</p>" +
            table +
            $"<p>Total vencido: {total}</p>" +
            $"<p>{closing}</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }
}
