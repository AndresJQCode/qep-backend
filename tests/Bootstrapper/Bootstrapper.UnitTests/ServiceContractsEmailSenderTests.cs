using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.ServiceContracts.Application;
using Modules.Notifications.Application;

namespace Bootstrapper.UnitTests;

public sealed class ServiceContractsEmailSenderTests
{
    private static readonly ServiceContractsEmailFormat Format = new("America/Bogota", "dd/MM/yyyy", "1.234,56");

    private static ServiceContractsCustomerMessage Message(ServiceContractsMessageKind kind, ServiceContractsEmailFormat? format = null) => new(
        kind,
        "ana@cliente.co",
        "Ana",
        "Verde SAS",
        [new ServiceContractsMessageLine("Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), kind == ServiceContractsMessageKind.Overdue ? 3 : 0)],
        [new CurrencyAmountDto("COP", 150000m)],
        new DateOnly(2026, 3, 10),
        format ?? Format);

    private static ServiceContractsCustomerMessage TwoCurrencies(ServiceContractsEmailFormat format) => Message(ServiceContractsMessageKind.Reminder, format) with
    {
        Lines =
        [
            new ServiceContractsMessageLine("Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), 0),
            new ServiceContractsMessageLine("Dominio", 1250.5m, "USD", new DateOnly(2026, 3, 12), 0),
        ],
        TotalByCurrency = [new CurrencyAmountDto("COP", 150000m), new CurrencyAmountDto("USD", 1250.5m)],
    };

    [Fact]
    public async Task ASuccessfulSendIsSentAndCarriesTheRecipient()
    {
        var channel = new RecordingChannel();
        var sender = new ServiceContractsEmailSender(channel, NullLogger<ServiceContractsEmailSender>.Instance);

        var result = await sender.SendAsync(Message(ServiceContractsMessageKind.Reminder), CancellationToken.None);

        Assert.IsType<ServiceContractsSendResult.Sent>(result);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal("ana@cliente.co", sent.ToAddress);
        Assert.Equal("Tu servicio Hosting vence el 10/03/2026", sent.Subject);
    }

    [Fact]
    public async Task TheKindPicksTheTemplate()
    {
        var channel = new RecordingChannel();
        var sender = new ServiceContractsEmailSender(channel, NullLogger<ServiceContractsEmailSender>.Instance);

        await sender.SendAsync(Message(ServiceContractsMessageKind.Overdue), CancellationToken.None);

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
        var sender = new ServiceContractsEmailSender(channel, NullLogger<ServiceContractsEmailSender>.Instance);

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
        var sender = new ServiceContractsEmailSender(channel, NullLogger<ServiceContractsEmailSender>.Instance);

        await sender.SendAsync(TwoCurrencies(new ServiceContractsEmailFormat("America/Bogota", "", "")), CancellationToken.None);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal("Tus servicios vencen el 10/03/2026", sent.Subject);
        Assert.Contains("Total: $ 150.000 y US$ 1.250,50", sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChannelExceptionBecomesFailedSendFailed()
    {
        var sender = new ServiceContractsEmailSender(new RecordingChannel { Throw = true }, NullLogger<ServiceContractsEmailSender>.Instance);

        var result = await sender.SendAsync(Message(ServiceContractsMessageKind.Reminder), CancellationToken.None);

        var failed = Assert.IsType<ServiceContractsSendResult.Failed>(result);
        Assert.Equal("send_failed", failed.Code);
    }

    /// <summary>
    /// Final review: un <c>send_failed</c> sin log no deja rastro de qué falló. Se registra el tipo de la excepción y la clase de
    /// mensaje, nunca la dirección ni el mensaje de la excepción (pueden traer datos personales), ni la excepción misma: el
    /// proveedor de logs imprimiría su <c>Message</c>.
    /// </summary>
    [Theory]
    [InlineData(ServiceContractsMessageKind.Reminder)]
    [InlineData(ServiceContractsMessageKind.Overdue)]
    public async Task AChannelExceptionIsLoggedWithItsTypeAndKindButNoPersonalData(ServiceContractsMessageKind kind)
    {
        var logger = new RecordingLogger();
        var sender = new ServiceContractsEmailSender(new RecordingChannel { Throw = true }, logger);

        await sender.SendAsync(Message(kind), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.Contains(kind.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@cliente.co", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordingChannel.FailureMessage, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADigestChannelExceptionIsLoggedWithItsTypeAndKindButNoPersonalData()
    {
        var logger = new RecordingLogger();
        var sender = new ServiceContractsEmailSender(new RecordingChannel { Throw = true }, logger);

        var result = await sender.SendDigestAsync(
            new ServiceContractsDigestEmail("luis@verde.co", "Luis", "Verde SAS", [], [], [], [], Format), CancellationToken.None);

        Assert.Equal("send_failed", Assert.IsType<ServiceContractsSendResult.Failed>(result).Code);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.Contains("Digest", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("luis@verde.co", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordingChannel.FailureMessage, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADigestIsSentToTheMemberWithTheCatalogAmounts()
    {
        var channel = new RecordingChannel();
        var sender = new ServiceContractsEmailSender(channel, NullLogger<ServiceContractsEmailSender>.Instance);
        var row = new ServiceContractsDigestRow("Ana", "Dominio", 20m, "USD", new DateOnly(2026, 3, 1), null);

        var result = await sender.SendDigestAsync(
            new ServiceContractsDigestEmail("luis@verde.co", "Luis", "Verde SAS", [], [row], [], [new CurrencyAmountDto("USD", 20m)], Format),
            CancellationToken.None);

        Assert.IsType<ServiceContractsSendResult.Sent>(result);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal("luis@verde.co", sent.ToAddress);
        Assert.Contains("- Ana / Dominio: US$ 20,00, 01/03/2026\n", sent.TextBody, StringComparison.Ordinal);
        Assert.Contains("Total vencido: US$ 20,00", sent.TextBody, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger<ServiceContractsEmailSender>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class RecordingChannel : IEmailChannel
    {
        // Lo que un proveedor real puede meter en el mensaje: la dirección rechazada.
        public const string FailureMessage = "provider down for ana@cliente.co";

        public List<EmailMessage> Sent { get; } = [];

        public bool Throw { get; init; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
