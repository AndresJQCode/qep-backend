using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El Strategy de envío (spec 2026-10-21 D17): cada canal sale por su proveedor, y el teléfono se valida igual en los dos.</summary>
public sealed class WhatsAppSenderRouterTests
{
    private static readonly WhatsAppQuotationMessage Message = new(
        ToPhone: "300 123 4567",
        FullName: "Juan Pérez",
        OrderNumber: "COT-000123",
        Total: 2450000m,
        Currency: "COP",
        ValidUntil: new DateOnly(2026, 9, 30),
        DocumentUrl: null);

    private static readonly WhatsAppOrderMessage OrderMessage = new(
        ToPhone: "3001234567",
        FullName: "Juan Pérez",
        OrderNumber: "PED-000123",
        Total: 2450000m,
        Currency: "COP",
        DocumentUrl: "https://assets.example.co/pedido.pdf");

    [Fact]
    public async Task AZenviaChannelGoesToZenvia()
    {
        var (router, zenvia, meta) = NewRouter();
        var channel = WhatsAppChannels.Any();

        await router.SendQuotationAsync(channel, Message, TestContext.Current.CancellationToken);

        var sent = Assert.Single(zenvia.Quotations);
        Assert.Same(channel, sent.Channel);
        Assert.Same(Message, sent.Message);
        Assert.Empty(meta.Quotations);
    }

    [Fact]
    public async Task AMetaChannelGoesToMetaWithTheNormalizedPhone()
    {
        var (router, zenvia, meta) = NewRouter();
        var channel = WhatsAppChannels.Meta();

        await router.SendQuotationAsync(channel, Message, TestContext.Current.CancellationToken);

        var sent = Assert.Single(meta.Quotations);
        Assert.Same(channel, sent.Channel);
        Assert.Equal("573001234567", sent.WaId);
        Assert.Same(Message, sent.Message);
        Assert.Empty(zenvia.Quotations);
    }

    [Theory]
    [InlineData("zenvia")]
    [InlineData("meta")]
    public async Task AQuotationWithoutPhoneIsRecipientMissingOnEitherProvider(string provider)
    {
        var (router, zenvia, meta) = NewRouter();
        WhatsAppChannel channel = provider == "zenvia" ? WhatsAppChannels.Any() : WhatsAppChannels.Meta();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            router.SendQuotationAsync(channel, Message with { ToPhone = " " }, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.recipient_missing", error.Code);
        Assert.Empty(zenvia.Quotations);
        Assert.Empty(meta.Quotations);
    }

    [Fact]
    public async Task AnOrderGoesToZenvia()
    {
        var (router, zenvia, _) = NewRouter();
        var channel = WhatsAppChannels.Any();

        await router.SendOrderAsync(channel, OrderMessage, TestContext.Current.CancellationToken);

        var sent = Assert.Single(zenvia.Orders);
        Assert.Same(channel, sent.Channel);
    }

    // Los pedidos no salen por Meta (D12): `ForOrder` ya los descartó, así que llegar aquí es un error de programación.
    [Fact]
    public async Task AnOrderOnAMetaChannelIsAProgrammingError()
    {
        var (router, zenvia, meta) = NewRouter();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.SendOrderAsync(WhatsAppChannels.Meta(), OrderMessage, TestContext.Current.CancellationToken));

        Assert.Empty(zenvia.Orders);
        Assert.Empty(meta.Quotations);
    }

    private static (WhatsAppSenderRouter Router, RecordingZenviaSender Zenvia, RecordingMetaSender Meta) NewRouter()
    {
        var zenvia = new RecordingZenviaSender();
        var meta = new RecordingMetaSender();
        return (new WhatsAppSenderRouter(zenvia, meta), zenvia, meta);
    }
}
