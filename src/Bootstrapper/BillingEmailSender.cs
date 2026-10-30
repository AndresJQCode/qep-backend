using Modules.Billing.Application;
using Modules.Notifications.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Los puertos de correo de Billing sobre <see cref="IEmailChannel"/> y las plantillas de Notifications. Billing no ve
/// Notifications ni al revés (ruling F7), así que este adaptador traduce los mensajes de Billing a los inputs de las plantillas
/// y el formato regional a <see cref="EmailRegionalFormat"/>. Cualquier excepción del canal es <c>send_failed</c>: el worker
/// reintenta o lo deja en el digest, nunca se cae por el correo.
/// </summary>
internal sealed class BillingEmailSender(IEmailChannel channel) : IBillingCustomerEmail, IBillingStaffEmail
{
    public async Task<BillingSendResult> SendAsync(BillingCustomerMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var format = ToFormat(message.Format);
            var input = new BillingCustomerEmailInput(
                message.Kind == BillingMessageKind.Overdue ? BillingEmailKind.Overdue : BillingEmailKind.Reminder,
                message.ToAddress,
                message.CustomerName,
                message.TenantName,
                message.Lines
                    .Select(line => new BillingEmailLine(line.ServiceName, line.Amount, line.Currency, line.DueDate, line.DaysOverdue))
                    .ToArray(),
                ToTotals(message.TotalByCurrency),
                message.NearestDueDate);

            var email = message.Kind == BillingMessageKind.Overdue
                ? BillingOverdueEmailTemplate.Render(input, format)
                : BillingReminderEmailTemplate.Render(input, format);

            await channel.SendAsync(email, cancellationToken);
            return new BillingSendResult.Sent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BillingSendResult.Failed("send_failed");
        }
    }

    public async Task<BillingSendResult> SendDigestAsync(BillingDigestEmail digest, CancellationToken cancellationToken)
    {
        try
        {
            var input = new BillingDigestEmailInput(
                digest.ToAddress,
                digest.MemberName,
                digest.TenantName,
                ToRows(digest.DueSoon),
                ToRows(digest.Overdue),
                ToRows(digest.FailedReminders),
                ToTotals(digest.OverdueTotalByCurrency));

            await channel.SendAsync(BillingDigestEmailTemplate.Render(input, ToFormat(digest.Format)), cancellationToken);
            return new BillingSendResult.Sent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BillingSendResult.Failed("send_failed");
        }
    }

    // EmailRegionalFormats.For es internal de Notifications.Infrastructure; se arma igual con los helpers públicos de Tenancy
    // que ese método usa, así el correo respeta el mismo patrón y los mismos separadores que el resto de la app.
    private static EmailRegionalFormat ToFormat(BillingEmailFormat format) =>
        new(
            TimeZoneInfo.FindSystemTimeZoneById(format.TimeZoneId),
            RegionalDateFormat.Pattern(format.DateFormat),
            RegionalNumberFormat.ToNumberFormatInfo(format.NumberFormat));

    private static BillingEmailTotal[] ToTotals(IReadOnlyList<CurrencyAmountDto> totals) =>
        totals.Select(total => new BillingEmailTotal(total.Currency, total.Amount)).ToArray();

    private static BillingDigestEmailRow[] ToRows(IReadOnlyList<BillingDigestRow> rows) =>
        rows.Select(row => new BillingDigestEmailRow(
                row.CustomerName, row.ServiceName, row.Amount, row.Currency, row.DueDate, row.FailureCode))
            .ToArray();
}
