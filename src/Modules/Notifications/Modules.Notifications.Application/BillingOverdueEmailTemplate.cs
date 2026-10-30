namespace Modules.Notifications.Application;

/// <summary>El aviso firme de cobros vencidos (spec cobros-recurrentes §3.3): lleva los días de atraso de cada uno.</summary>
public static class BillingOverdueEmailTemplate
{
    public const string TemplateRef = "billing.overdue.v1";

    public static EmailMessage Render(BillingCustomerEmailInput input, EmailRegionalFormat format)
    {
        var subject = $"Tienes {input.Lines.Count} cobro(s) vencido(s)";
        var total = BillingEmailParts.Totals(input.TotalByCurrency);
        const string closing = "Escríbenos para ponerte al día.";
        // El aviso sale antes de que alguien registre el pago en QEP: quien ya pagó no tiene por qué leerlo como un reclamo.
        const string alreadyPaid = "Si ya pagaste, ignora este mensaje.";

        var textBody =
            $"Hola {input.CustomerName},\n\n" +
            $"{input.TenantName} tiene registrados estos cobros vencidos a tu nombre:\n\n" +
            string.Concat(input.Lines.Select(line =>
                $"- {line.ServiceName}: {line.Amount}, venció el {BillingEmailParts.Date(line.DueDate, format)} ({line.DaysOverdue} días de atraso)\n")) +
            $"\nTotal vencido: {total}\n\n{closing}\n{alreadyPaid}\n";

        var table = BillingEmailParts.HtmlTable(
            ["Servicio", "Monto", "Venció", "Días de atraso"],
            input.Lines.Select(line => new[]
            {
                line.ServiceName,
                line.Amount,
                BillingEmailParts.Date(line.DueDate, format),
                line.DaysOverdue.ToString(format.Numbers),
            }));

        var htmlBody =
            $"<p>Hola {BillingEmailParts.Html(input.CustomerName)},</p>" +
            $"<p><strong>{BillingEmailParts.Html(input.TenantName)}</strong> tiene registrados estos cobros vencidos a tu nombre:</p>" +
            table +
            $"<p>Total vencido: {total}</p>" +
            $"<p>{closing}</p>" +
            $"<p>{alreadyPaid}</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }
}
