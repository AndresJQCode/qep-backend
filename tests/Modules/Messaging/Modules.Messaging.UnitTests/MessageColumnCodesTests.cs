using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §7.1: la única traducción smallint ↔ enum, de ida y vuelta, y los CHECK de
/// la tabla coinciden con los valores.</summary>
public sealed class MessageColumnCodesTests
{
    [Fact]
    public void EveryEnumValueRoundTrips()
    {
        foreach (var direction in Enum.GetValues<MessageDirection>())
        {
            Assert.Equal(direction, MessageColumnCodes.ToDirection(MessageColumnCodes.ToCode(direction)));
        }

        foreach (var kind in Enum.GetValues<MessageKind>())
        {
            Assert.Equal(kind, MessageColumnCodes.ToKind(MessageColumnCodes.ToCode(kind)));
        }

        foreach (var status in Enum.GetValues<MessageStatus>())
        {
            Assert.Equal(status, MessageColumnCodes.ToStatus(MessageColumnCodes.ToCode(status)));
        }
    }

    [Fact]
    public void TheCodesAreTheOnesOfTheSpec()
    {
        Assert.Equal((short)1, MessageColumnCodes.ToCode(MessageDirection.Inbound));
        Assert.Equal((short)2, MessageColumnCodes.ToCode(MessageDirection.Outbound));
        Assert.Equal((short)12, MessageColumnCodes.ToCode(MessageKind.Unsupported));
        Assert.Equal((short)4, MessageColumnCodes.ToCode(MessageStatus.Failed));
        Assert.Equal("direction IN (1, 2)", MessagingDbContext.DirectionCheck);
        Assert.Equal("kind BETWEEN 1 AND 12", MessagingDbContext.KindCheck);
        Assert.Equal("status BETWEEN 1 AND 4", MessagingDbContext.StatusCheck);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void AnUnknownCodeIsLoudNotSilent(short code) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageColumnCodes.ToKind(code));
}
