using Modules.Billing.Application;
using Modules.Notifications.Application;

namespace Bootstrapper.UnitTests;

public sealed class BillingEmailSenderTests
{
    private static readonly BillingEmailFormat Format = new("America/Bogota", "dd/MM/yyyy", "1.234,56");

    private static BillingCustomerMessage Message(BillingMessageKind kind) => new(
        kind,
        "ana@cliente.co",
        "Ana",
        "Verde SAS",
        [new BillingMessageLine("Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), kind == BillingMessageKind.Overdue ? 3 : 0)],
        [new CurrencyAmountDto("COP", 150000m)],
        new DateOnly(2026, 3, 10),
        Format);

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

    [Fact]
    public async Task AChannelExceptionBecomesFailedSendFailed()
    {
        var sender = new BillingEmailSender(new RecordingChannel { Throw = true });

        var result = await sender.SendAsync(Message(BillingMessageKind.Reminder), CancellationToken.None);

        var failed = Assert.IsType<BillingSendResult.Failed>(result);
        Assert.Equal("send_failed", failed.Code);
    }

    [Fact]
    public async Task ADigestIsSentToTheMember()
    {
        var channel = new RecordingChannel();
        var sender = new BillingEmailSender(channel);
        var row = new BillingDigestRow("Ana", "Hosting", 10m, "COP", new DateOnly(2026, 3, 10), null);

        var result = await sender.SendDigestAsync(
            new BillingDigestEmail("luis@verde.co", "Luis", "Verde SAS", [row], [], [], [], Format),
            CancellationToken.None);

        Assert.IsType<BillingSendResult.Sent>(result);
        Assert.Equal("luis@verde.co", Assert.Single(channel.Sent).ToAddress);
    }

    [Fact]
    public async Task TheUnavailableWhatsAppOutboundFailsWithChannelNotConfigured()
    {
        var outbound = new BillingWhatsAppOutboundUnavailable();

        var result = await outbound.SendAsync(Message(BillingMessageKind.Reminder), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("channel_not_configured", Assert.IsType<BillingSendResult.Failed>(result).Code);
        Assert.Empty(await outbound.ListConnectionsAsync(Guid.NewGuid(), CancellationToken.None));
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
