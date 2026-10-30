using Modules.Billing.Application;
using Modules.Notifications.Application;

namespace Bootstrapper.UnitTests;

public sealed class BillingEmailSenderTests
{
    private static readonly BillingEmailFormat Format = new("America/Bogota", "dd/MM/yyyy", "1.234,56");

    private static BillingCustomerMessage Message(BillingMessageKind kind, BillingEmailFormat? format = null) => new(
        kind,
        "ana@cliente.co",
        "Ana",
        "Verde SAS",
        [new BillingMessageLine("Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), kind == BillingMessageKind.Overdue ? 3 : 0)],
        [new CurrencyAmountDto("COP", 150000m)],
        new DateOnly(2026, 3, 10),
        format ?? Format);

    private static BillingCustomerMessage TwoCurrencies(BillingEmailFormat format) => Message(BillingMessageKind.Reminder, format) with
    {
        Lines =
        [
            new BillingMessageLine("Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), 0),
            new BillingMessageLine("Dominio", 1250.5m, "USD", new DateOnly(2026, 3, 12), 0),
        ],
        TotalByCurrency = [new CurrencyAmountDto("COP", 150000m), new CurrencyAmountDto("USD", 1250.5m)],
    };

    [Fact]
    public async Task ASuccessfulSendIsSentAndCarriesTheRecipient()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);

        var result = await sender.SendAsync(Message(BillingMessageKind.Reminder), CancellationToken.None);

        Assert.IsType<BillingSendResult.Sent>(result);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal("ana@cliente.co", sent.ToAddress);
        Assert.Equal("Tu servicio Hosting vence el 10/03/2026", sent.Subject);
    }

    [Fact]
    public async Task TheKindPicksTheTemplate()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);

        await sender.SendAsync(Message(BillingMessageKind.Overdue), CancellationToken.None);

        Assert.Equal("Tienes 1 cobro(s) vencido(s)", Assert.Single(channel.Sent).Subject);
    }

    /// <summary>
    /// El mismo monto que la plantilla de WhatsApp de una cotización (<c>WhatsAppTemplateValues.Total</c>): símbolo y decimales
    /// del catálogo de monedas de Tenancy, separadores del tenant.
    /// </summary>
    [Fact]
    public async Task AmountsUseTheCurrencyCatalogAndTheTenantSeparators()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);

        await sender.SendAsync(TwoCurrencies(Format), CancellationToken.None);
        await sender.SendAsync(TwoCurrencies(Format with { NumberFormat = "1,234.56" }), CancellationToken.None);

        var (comma, dot) = (channel.Sent[0].TextBody, channel.Sent[1].TextBody);
        Assert.Contains("- Dominio: US$ 1.250,50, vence el 12/03/2026\n", comma, StringComparison.Ordinal);
        Assert.Contains("Total: $ 150.000 y US$ 1.250,50", comma, StringComparison.Ordinal);
        Assert.Contains("Total: $ 150,000 y US$ 1,250.50", dot, StringComparison.Ordinal);
    }

    /// <summary>
    /// El procesador manda formato y separadores vacíos cuando el tenant no tiene fila regional; el correo cae en el formato
    /// colombiano, igual que <c>EmailRegionalFormats.For</c> con la configuración en null.
    /// </summary>
    [Fact]
    public async Task AnEmptyRegionalFormatFallsBackToTheColombianOne()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);

        await sender.SendAsync(TwoCurrencies(new BillingEmailFormat("America/Bogota", "", "")), CancellationToken.None);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal("Tus servicios vencen el 10/03/2026", sent.Subject);
        Assert.Contains("Total: $ 150.000 y US$ 1.250,50", sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChannelExceptionBecomesFailedSendFailed()
    {
        var sender = new BillingEmailSender(new RecordingChannel { Throw = true });

        var result = await sender.SendAsync(Message(BillingMessageKind.Reminder), CancellationToken.None);

        var failed = Assert.IsType<BillingSendResult.Failed>(result);
        Assert.Equal("send_failed", failed.Code);
    }

    [Fact]
    public async Task ADigestIsSentToTheMemberWithTheCatalogAmounts()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);
        var row = new BillingDigestRow("Ana", "Dominio", 20m, "USD", new DateOnly(2026, 3, 1), null);

        var result = await sender.SendDigestAsync(
            new BillingDigestEmail("luis@verde.co", "Luis", "Verde SAS", [], [row], [], [new CurrencyAmountDto("USD", 20m)], Format),
            CancellationToken.None);

        Assert.IsType<BillingSendResult.Sent>(result);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal("luis@verde.co", sent.ToAddress);
        Assert.Contains("- Ana / Dominio: US$ 20,00, 01/03/2026\n", sent.TextBody, StringComparison.Ordinal);
        Assert.Contains("Total vencido: US$ 20,00", sent.TextBody, StringComparison.Ordinal);
    }

    private sealed class RecordingChannel : IEmailChannel
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool Throw { get; init; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("provider down");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
