using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

public sealed class ExternalMessageStateTests
{
    [Theory]
    [InlineData(MessageStatus.Sent, "Sent")]
    [InlineData(MessageStatus.Delivered, "Delivered")]
    [InlineData(MessageStatus.Read, "Read")]
    public void WithoutAFailureThereIsNoCodeNorReason(MessageStatus status, string expected) =>
        Assert.Equal(new ExternalMessageState(expected, null, null), ExternalMessageState.Describe(status, null));

    [Fact]
    public void ACertainFailureUsesTheTextOfTheBubble() =>
        Assert.Equal(
            new ExternalMessageState("Failed", 131042, MessageFailureReasons.For(131042)),
            ExternalMessageState.Describe(MessageStatus.Failed, 131042));

    [Fact]
    public void AnUnconfirmedSendDoesNotInviteToRetry()
    {
        var state = ExternalMessageState.Describe(MessageStatus.Failed, MessageFailureReasons.Unconfirmed);
        Assert.Equal(("Unconfirmed", -1), (state.Status, state.FailureCode));
        Assert.DoesNotContain("Intenta de nuevo", state.FailureReason, StringComparison.Ordinal);
    }
}
