using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Purchases.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Purchases → Notifications: la orden de compra por correo sobre <c>IEmailChannel</c>. Un fallo del canal es <c>Failed</c> y
/// nunca llega al llamador como excepción; el log lleva sólo el tipo de la excepción, jamás la dirección.
/// </summary>
public sealed class PurchaseOrderEmailSenderTests
{
    private const string Address = "ventas@textiles.co";

    private static PurchaseOrderEmailRequest Request() => new(
        Address,
        new PurchaseOrderPdfDocument(
            "Verde SAS",
            "OC-0007",
            "Enviada",
            "18/11/2026",
            "Textiles del Valle",
            "NIT 900123456",
            "COP",
            "Entregar en bodega norte",
            [new PurchaseOrderPdfLine("CAM-M-AZ", "Camiseta", "24", "$ 36.900", "19 %", "$ 885.600")],
            "$ 885.600",
            "$ 168.264",
            "$ 1.053.864",
            ShowsConfirmedValues: false),
        [1, 2, 3],
        "OC-0007.pdf");

    [Fact]
    public async Task ASuccessfulSendIsSentWithTheRenderedMessage()
    {
        var channel = new FakeChannel();
        var sender = new PurchaseOrderEmailSender(channel, new ListLogger<PurchaseOrderEmailSender>());

        var result = await sender.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(PurchaseOrderEmailResult.Sent, result);
        var message = Assert.Single(channel.Sent);
        Assert.Equal(Address, message.ToAddress);
        Assert.Equal("Orden de compra OC-0007 de Verde SAS", message.Subject);
        Assert.Equal("OC-0007.pdf", Assert.Single(message.Attachments!).FileName);
    }

    [Fact]
    public async Task ADocumentWithoutAmountsGoesOutWithoutTheAmountsSection()
    {
        var channel = new FakeChannel();
        var sender = new PurchaseOrderEmailSender(channel, new ListLogger<PurchaseOrderEmailSender>());
        var request = Request();
        request = request with
        {
            Document = request.Document with
            {
                Lines = [new PurchaseOrderPdfLine("CAM-M-AZ", "Camiseta", "24", "—", "19 %", "—")],
                Subtotal = "—",
                Tax = "—",
                Total = "—",
                ShowsAmounts = false,
            },
        };

        var result = await sender.SendAsync(request, CancellationToken.None);

        Assert.Equal(PurchaseOrderEmailResult.Sent, result);
        var message = Assert.Single(channel.Sent);
        Assert.Contains("CAM-M-AZ", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("—", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Total", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChannelExceptionBecomesFailedAndIsLoggedWithoutTheAddress()
    {
        var logger = new ListLogger<PurchaseOrderEmailSender>();
        var sender = new PurchaseOrderEmailSender(new FakeChannel { Throw = true }, logger);

        var result = await sender.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(PurchaseOrderEmailResult.Failed, result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeChannel.FailureMessage, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChannelTimeoutWithoutAClientCancellationBecomesFailed()
    {
        var sender = new PurchaseOrderEmailSender(new FakeChannel { TimeOut = true }, new ListLogger<PurchaseOrderEmailSender>());

        var result = await sender.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(PurchaseOrderEmailResult.Failed, result);
    }

    [Fact]
    public async Task ACancelledRequestStillPropagatesTheCancellation()
    {
        var sender = new PurchaseOrderEmailSender(new FakeChannel { TimeOut = true }, new ListLogger<PurchaseOrderEmailSender>());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Request(), cancelled.Token));
    }

    private sealed class FakeChannel : IEmailChannel
    {
        public const string FailureMessage = "provider said ventas@textiles.co is blocked";

        public List<EmailMessage> Sent { get; } = [];

        public bool Throw { get; init; }

        public bool TimeOut { get; init; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (TimeOut)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            }

            if (Throw)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
