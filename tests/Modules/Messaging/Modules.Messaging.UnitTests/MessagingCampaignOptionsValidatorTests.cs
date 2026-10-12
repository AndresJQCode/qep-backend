using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 decisión 14 y §10, con la decisión del owner 2026-10-12 (P3): <c>Messaging:Campaigns:MessagesPerSecond</c> (20 por
/// defecto) es el ritmo inicial y objetivo; <c>MaxMessagesPerSecond</c> (80, el throughput por defecto de Meta) es el techo. Vale
/// 1 ≤ objetivo ≤ techo ≤ 1000.</summary>
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
    public void ATargetBelowOneOrAboveTheDefaultCeilingIsRejected(int value) => Assert.False(IsValid(value));

    [Fact]
    public void TheDefaultCeilingIs80() => Assert.Equal(80, new MessagingCampaignOptions().MaxMessagesPerSecond);

    private static bool IsValid(int target, int max) =>
        new MessagingCampaignOptionsValidator().Validate(null, new MessagingCampaignOptions { MessagesPerSecond = target, MaxMessagesPerSecond = max }).Succeeded;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(150, 200)]
    [InlineData(1000, 1000)]
    public void ATargetUpToACustomCeilingIsValid(int target, int max) => Assert.True(IsValid(target, max));

    [Theory]
    [InlineData(100, 99)]
    [InlineData(10, 0)]
    [InlineData(10, 1001)]
    [InlineData(1001, 1001)]
    public void ATargetAboveTheCeilingOrACeilingOutsideOneToAThousandIsRejected(int target, int max) => Assert.False(IsValid(target, max));
}
