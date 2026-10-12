using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 decisión 14 y §10: <c>Messaging:Campaigns:MessagesPerSecond</c> es opcional (20 por defecto) y va de 1 a 80, el
/// mínimo por número que Meta documenta: pasar de ahí dejaría a la campaña pisando el throughput de la bandeja con 130429.</summary>
public sealed class MessagingCampaignOptionsValidatorTests
{
    private static bool IsValid(int messagesPerSecond) =>
        new MessagingCampaignOptionsValidator().Validate(null, new MessagingCampaignOptions { MessagesPerSecond = messagesPerSecond }).Succeeded;

    [Fact]
    public void TheDefaultIs20AndValid()
    {
        Assert.Equal(20, new MessagingCampaignOptions().MessagesPerSecond);
        Assert.True(IsValid(new MessagingCampaignOptions().MessagesPerSecond));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    public void TheBoundsAreValid(int value) => Assert.True(IsValid(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(81)]
    public void OutsideOneToEightyIsRejected(int value) => Assert.False(IsValid(value));
}
