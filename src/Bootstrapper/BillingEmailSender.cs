using Microsoft.Extensions.Logging;
using Modules.Billing.Application;
using Modules.Notifications.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Los puertos de correo de Billing sobre <see cref="IEmailChannel"/> y las plantillas de Notifications. Billing no ve
/// Notifications ni al revés (ruling F7), así que este adaptador traduce los mensajes de Billing a los inputs de las plantillas
/// y el formato regional a <see cref="EmailRegionalFormat"/>. Cualquier excepción del canal es <c>send_failed</c>: el worker
/// reintenta o lo deja en el digest, nunca se cae por el correo.
/// </summary>
internal sealed partial class BillingEmailSender(IEmailChannel channel, ILogger<BillingEmailSender> logger)
    : IBillingCustomerEmail, IBillingStaffEmail
{
    private const string DigestKind = "Digest";

    // Sólo el tipo de la excepción y la clase de mensaje: el Message del proveedor y la excepción misma (que el proveedor de logs
    // imprimiría entera) pueden traer la dirección del destinatario.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing email of kind {MessageKind} failed with {ExceptionType}; it is reported as send_failed.")]
    private static partial void LogSendFailed(ILogger logger, string messageKind, string exceptionType);

    public async Task<BillingSendResult> SendAsync(BillingCustomerMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var format = ToFormat(message.Format);
            var numberFormat = message.Format.NumberFormat;
            var input = new BillingCustomerEmailInput(
                message.Kind == BillingMessageKind.Overdue ? BillingEmailKind.Overdue : BillingEmailKind.Reminder,
                message.ToAddress,
                message.CustomerName,
                message.TenantName,
                message.Lines
                    .Select(line => new BillingEmailLine(
                        line.ServiceName, Money(line.Amount, line.Currency, numberFormat), line.DueDate, line.DaysOverdue))
                    .ToArray(),
                ToTotals(message.TotalByCurrency, numberFormat),
                message.NearestDueDate);

            var email = message.Kind == BillingMessageKind.Overdue
                ? BillingOverdueEmailTemplate.Render(input, format)
                : BillingReminderEmailTemplate.Render(input, format);

            await channel.SendAsync(email, cancellationToken);
            return new BillingSendResult.Sent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(logger, message.Kind.ToString(), exception.GetType().Name);
            return new BillingSendResult.Failed("send_failed");
        }
    }

    public async Task<BillingSendResult> SendDigestAsync(BillingDigestEmail digest, CancellationToken cancellationToken)
    {
        try
        {
            var numberFormat = digest.Format.NumberFormat;
            var input = new BillingDigestEmailInput(
                digest.ToAddress,
                digest.MemberName,
                digest.TenantName,
                ToRows(digest.DueSoon, numberFormat),
                ToRows(digest.Overdue, numberFormat),
                ToRows(digest.FailedReminders, numberFormat),
                ToTotals(digest.OverdueTotalByCurrency, numberFormat));

            await channel.SendAsync(BillingDigestEmailTemplate.Render(input, ToFormat(digest.Format)), cancellationToken);
            return new BillingSendResult.Sent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(logger, DigestKind, exception.GetType().Name);
            return new BillingSendResult.Failed("send_failed");
        }
    }

    // Copia de `EmailRegionalFormats.For` (Notifications.Infrastructure/Messaging/EmailRegionalFormats.cs): ese helper es
    // `internal` de Notifications.Infrastructure y Bootstrapper no lo ve, así que se arma con los mismos dos helpers públicos de
    // Tenancy que él usa. Si `For` cambia de regla, esto cambia con él.
    // El procesador manda "" en DateFormat y NumberFormat cuando el tenant no tiene fila regional, en vez de null: el record de
    // Billing no admite null y "" es un valor que `RegionalDateFormat.Pattern` y `RegionalNumberFormat.ToNumberFormatInfo` no
    // reconocen, así que cae en su default colombiano. Es el mismo resultado que `For(zone, null)` → `EmailRegionalFormat.Colombian`.
    private static EmailRegionalFormat ToFormat(BillingEmailFormat format) =>
        new(
            TimeZoneInfo.FindSystemTimeZoneById(format.TimeZoneId),
            RegionalDateFormat.Pattern(format.DateFormat),
            RegionalNumberFormat.ToNumberFormatInfo(format.NumberFormat));

    // El monto de un correo de cobro se escribe igual que en la plantilla de WhatsApp de una cotización: símbolo y decimales del
    // catálogo de monedas de Tenancy, separadores del tenant («US$ 20,00»). Se reutiliza ese formateador en vez de tener uno más.
    private static string Money(decimal amount, string currency, string numberFormat) =>
        WhatsAppTemplateValues.Total(amount, currency, numberFormat);

    private static string[] ToTotals(IReadOnlyList<CurrencyAmountDto> totals, string numberFormat) =>
        totals.Select(total => Money(total.Amount, total.Currency, numberFormat)).ToArray();

    private static BillingDigestEmailRow[] ToRows(IReadOnlyList<BillingDigestRow> rows, string numberFormat) =>
        rows.Select(row => new BillingDigestEmailRow(
                row.CustomerName, row.ServiceName, Money(row.Amount, row.Currency, numberFormat), row.DueDate, row.FailureCode))
            .ToArray();
}
