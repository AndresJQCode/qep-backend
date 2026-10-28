using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.1 y §5.2. El sondeo por defecto es costo en reposo contra una base compartida y nadie
/// espera una entrega de Shopify de forma interactiva, así que arranca en 15 s; el validador sigue rechazando un cero.</summary>
public sealed class ShopifyWebhookOptionsTests
{
    private static bool IsValid(ShopifyWebhookOptions options) =>
        new ShopifyWebhookOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void TheDefaultPollIsFifteenSeconds() =>
        Assert.Equal(15, new ShopifyWebhookOptions().DeliveryPollSeconds);

    [Fact]
    public void TheDefaultsAreValid() =>
        Assert.True(IsValid(new ShopifyWebhookOptions()));

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1_048_576, 0)]
    [InlineData(1_048_576, -1)]
    public void ANonPositiveValueIsRejected(int maxBodyBytes, int deliveryPollSeconds) =>
        Assert.False(IsValid(new ShopifyWebhookOptions
        {
            MaxBodyBytes = maxBodyBytes,
            DeliveryPollSeconds = deliveryPollSeconds,
        }));
}
