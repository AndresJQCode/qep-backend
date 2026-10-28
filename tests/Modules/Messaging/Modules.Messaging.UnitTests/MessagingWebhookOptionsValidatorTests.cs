using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §9: el tope, la concurrencia y la cola del webhook son positivos; un cero dejaría
/// el webhook rechazando todo (413 o 429) y Meta reintentando por días.</summary>
public sealed class MessagingWebhookOptionsValidatorTests
{
    private static bool IsValid(MessagingWebhookOptions options) =>
        new MessagingWebhookOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void TheDefaultsAreValid() =>
        Assert.True(IsValid(new MessagingWebhookOptions()));

    [Theory]
    [InlineData(0, 64, 256)]
    [InlineData(-1, 64, 256)]
    [InlineData(4194304, 0, 256)]
    [InlineData(4194304, 64, 0)]
    [InlineData(4194304, 64, -5)]
    public void ANonPositiveValueIsRejected(int maxBodyBytes, int concurrencyLimit, int queueLimit) =>
        Assert.False(IsValid(new MessagingWebhookOptions
        {
            MaxBodyBytes = maxBodyBytes,
            ConcurrencyLimit = concurrencyLimit,
            QueueLimit = queueLimit,
        }));
}
