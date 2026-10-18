using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.1 y §12: con la firma válida, lo que no se puede encolar se descarta sólo si es
/// determinista (reintentar el mismo cuerpo da lo mismo) y con su motivo, para que el endpoint lo deje en el log. La cabecera del
/// dominio no va firmada: cuando el cuerpo trae el dominio, tienen que coincidir.</summary>
public sealed class ReceiveShopifyWebhookHandlerTests
{
    private const string Secret = "app-secret";
    private const string Shop = "mi-tienda.myshopify.com";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 16, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignedValidEnvelopeIsEnqueued()
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(Command("""{"id":1}"""), Ct);

        Assert.Equal(ShopifyWebhookReceipt.Enqueued, receipt);
        var envelope = Assert.Single(queue.Enqueued);
        Assert.Equal(("wh-1", "orders/create", Shop, "2026-10"), (envelope.WebhookId, envelope.Topic, envelope.ShopDomain, envelope.ApiVersion));
    }

    [Fact]
    public async Task UnsignedThrowsUnauthorizedWithoutEnqueueing()
    {
        var (handler, queue) = Handler();

        await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            handler.HandleAsync(Command("""{"id":1}""") with { Signature = null }, Ct));

        Assert.Empty(queue.Enqueued);
    }

    [Theory]
    [InlineData(null, "orders/create", Shop)]
    [InlineData("wh-1", null, Shop)]
    [InlineData("wh-1", "orders/create", "  ")]
    public async Task SignedWithoutARequiredHeaderIsDiscardedAsMissingHeaders(string? webhookId, string? topic, string? shop)
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(
            Command("""{"id":1}""") with { WebhookId = webhookId, Topic = topic, ShopDomain = shop }, Ct);

        Assert.Equal(ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.MissingHeaders), receipt);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task SignedWithAHeaderWiderThanItsColumnIsDiscarded()
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(Command("""{"id":1}""") with { Topic = new string('t', 65) }, Ct);

        Assert.Equal(ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.HeaderTooLong), receipt);
        Assert.Empty(queue.Enqueued);
    }

    // JsonDocument acepta "\u0000" y el jsonb de Postgres no; sin esto el INSERT da 500 y Shopify reintenta 48 horas.
    [Theory]
    [InlineData("""{"note":"hola\u0000mundo"}""")]
    [InlineData("""{"a\u0000b":1}""")]
    [InlineData("not json")]
    public async Task SignedBodyThatJsonbRejectsIsDiscardedAsInvalidJson(string body)
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(Command(body), Ct);

        Assert.Equal(ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.InvalidJson), receipt);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task SignedBodyWithAnEscapedBackslashBeforeUIsEnqueued()
    {
        var (handler, queue) = Handler();

        // «\\u0000» es una barra seguida del texto «u0000»: jsonb lo acepta y no es un NUL.
        var receipt = await handler.HandleAsync(Command("""{"note":"C:\\u0000"}"""), Ct);

        Assert.Equal(ShopifyWebhookReceipt.Enqueued, receipt);
        Assert.Single(queue.Enqueued);
    }

    // El cuerpo va firmado y la cabecera no. Un cuerpo de cumplimiento reenviado con el dominio de otra tienda se descarta.
    [Theory]
    [InlineData("""{"shop_id":1,"shop_domain":"otra-tienda.myshopify.com"}""")]
    [InlineData("""{"id":1,"myshopify_domain":"otra-tienda.myshopify.com"}""")]
    public async Task SignedPayloadWhoseShopDomainDiffersFromTheHeaderIsDiscarded(string body)
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(Command(body) with { Topic = "shop/redact" }, Ct);

        Assert.Equal(ShopifyWebhookReceipt.Discarded(ShopifyWebhookReceipt.ShopDomainMismatch), receipt);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task SignedPayloadWhoseShopDomainMatchesIgnoringCaseIsEnqueued()
    {
        var (handler, queue) = Handler();

        var receipt = await handler.HandleAsync(
            Command("""{"shop_id":1,"shop_domain":"Mi-Tienda.myshopify.com"}""") with { Topic = "shop/redact" }, Ct);

        Assert.Equal(ShopifyWebhookReceipt.Enqueued, receipt);
    }

    [Fact]
    public async Task SignedRetryWithTheSameWebhookIdIsADuplicate()
    {
        var (handler, queue) = Handler();
        await handler.HandleAsync(Command("""{"id":1}"""), Ct);

        var receipt = await handler.HandleAsync(Command("""{"id":1}"""), Ct);

        Assert.Equal(ShopifyWebhookReceipt.Duplicate, receipt);
        Assert.Single(queue.Enqueued);
    }

    private static ReceiveShopifyWebhookCommand Command(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), bytes));
        return new ReceiveShopifyWebhookCommand(bytes, signature, "orders/create", Shop, "wh-1", "2026-10");
    }

    private static (ReceiveShopifyWebhookHandler Handler, RecordingDeliveries Queue) Handler()
    {
        var queue = new RecordingDeliveries();
        return (new ReceiveShopifyWebhookHandler(new FixedSecret(), queue, new FakeClock(Now)), queue);
    }

    private sealed class FixedSecret : IShopifyAppSecret
    {
        public string? ClientSecret => Secret;
    }

    private sealed class RecordingDeliveries : IShopifyWebhookDeliveries
    {
        public List<ShopifyWebhookEnvelope> Enqueued { get; } = [];

        public Task<bool> EnqueueAsync(ShopifyWebhookEnvelope envelope, DateTimeOffset receivedAt, CancellationToken cancellationToken)
        {
            if (Enqueued.Any(existing => existing.WebhookId == envelope.WebhookId))
            {
                return Task.FromResult(false);
            }

            Enqueued.Add(envelope);
            return Task.FromResult(true);
        }

        public Task<int> DeleteByShopDomainAsync(string shopDomain, long exceptDeliveryId, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
