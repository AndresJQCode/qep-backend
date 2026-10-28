using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-26 §3.1 y §3.3: el bloqueo del contacto vive en la conversación y no mueve estado, asignación ni asistente.</summary>
public sealed class ConversationBlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MemberA = Guid.CreateVersion7();
    private static readonly Guid MemberB = Guid.CreateVersion7();

    /// <summary>El número que el handler le mandó a Meta.</summary>
    private const string Sent = "573001234567";

    private static Conversation WhatsApp(string? waId = "573001234567", DateTimeOffset? lastInboundAt = null, bool hasInbound = true) =>
        Conversation.ForTests(
            Conversation.StartWithUserId(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "CO.abc123", waId, "Laura", Now.AddDays(-2)),
            unreadCount: 0,
            lastInboundAt: hasInbound ? lastInboundAt ?? Now.AddHours(-1) : null);

    [Fact]
    public void BlockingKeepsStatusAssignmentUnreadAndAutomation()
    {
        var conversation = Conversation.ForTests(WhatsApp(), unreadCount: 3, lastInboundAt: Now.AddHours(-1));
        conversation.StartAutomation(Guid.CreateVersion7(), Now.AddMinutes(-30));
        conversation.Take(MemberB, Now.AddMinutes(-20));
        var version = conversation.Version;
        var status = conversation.Status;
        var assigned = conversation.AssignedMemberId;
        var automation = conversation.AutomationStatus;

        var ev = conversation.Block(MemberA, Sent, Now);

        Assert.Equal(Now, conversation.BlockedAt);
        Assert.Equal(MemberA, conversation.BlockedByMemberId);
        Assert.Equal("573001234567", conversation.BlockedWaId);
        Assert.True(conversation.IsBlocked);
        Assert.Equal(version + 1, conversation.Version);
        Assert.Equal(status, conversation.Status);
        Assert.Equal(assigned, conversation.AssignedMemberId);
        Assert.Equal(3, conversation.UnreadCount);
        Assert.Equal(automation, conversation.AutomationStatus);
        Assert.NotNull(ev);
        Assert.Equal(ConversationEventType.ContactBlocked, ev.Type);
        Assert.Equal(MemberA, ev.Actor);
    }

    // Un reintento por conflicto relee la conversación, y su wa_id pudo cambiar en el medio: se guarda el número que Meta bloqueó.
    [Fact]
    public void BlockingStoresTheNumberSentToMetaNotTheCurrentOne()
    {
        var conversation = WhatsApp(waId: "573009999999");

        conversation.Block(MemberA, Sent, Now);

        Assert.Equal(Sent, conversation.BlockedWaId);
    }

    [Fact]
    public void BlockingAResolvedConversationKeepsItResolved()
    {
        var conversation = WhatsApp();
        conversation.Resolve(Now.AddMinutes(-5));

        conversation.Block(MemberA, Sent, Now);

        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
        Assert.True(conversation.IsBlocked);
    }

    [Fact]
    public void BlockingTwiceIsANoOp()
    {
        var conversation = WhatsApp();
        conversation.Block(MemberA, Sent, Now);
        var version = conversation.Version;

        var second = conversation.Block(MemberB, Sent, Now.AddMinutes(1));

        Assert.Null(second);
        Assert.Equal(version, conversation.Version);
        Assert.Equal(MemberA, conversation.BlockedByMemberId);
    }

    [Fact]
    public void UnblockingClearsTheThreeFields()
    {
        var conversation = WhatsApp();
        conversation.Block(MemberA, Sent, Now);
        var version = conversation.Version;

        var ev = conversation.Unblock(MemberB, Now.AddMinutes(5));

        Assert.Null(conversation.BlockedAt);
        Assert.Null(conversation.BlockedByMemberId);
        Assert.Null(conversation.BlockedWaId);
        Assert.False(conversation.IsBlocked);
        Assert.Equal(version + 1, conversation.Version);
        Assert.NotNull(ev);
        Assert.Equal(ConversationEventType.ContactUnblocked, ev.Type);
        Assert.Equal(MemberB, ev.Actor);

        var other = WhatsApp();
        var otherVersion = other.Version;
        Assert.Null(other.Unblock(MemberB, Now));
        Assert.Equal(otherVersion, other.Version);
    }

    [Fact]
    public void EnsureCanBlockRefusesInstagramAndConversationsWithoutWaId()
    {
        var instagram = Conversation.ForTests(
            Conversation.StartWithUserId(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "17841400000000000", null, "Laura", Now.AddDays(-2),
                ConversationChannel.Instagram),
            unreadCount: 0,
            lastInboundAt: Now.AddHours(-1));
        var noWaId = WhatsApp(waId: null);

        foreach (var conversation in new[] { instagram, noWaId })
        {
            var version = conversation.Version;
            var ex = Assert.Throws<MessagingDomainException>(() => conversation.EnsureCanBlock(Now));
            Assert.Equal(MessagingErrorCodes.BlockUnsupportedChannel, ex.Code);
            Assert.Equal(version, conversation.Version);
        }
    }

    [Fact]
    public void EnsureCanBlockRefusesAClosedWindow()
    {
        foreach (var conversation in new[] { WhatsApp(lastInboundAt: Now.AddHours(-25)), WhatsApp(hasInbound: false) })
        {
            var ex = Assert.Throws<MessagingDomainException>(() => conversation.EnsureCanBlock(Now));
            Assert.Equal(MessagingErrorCodes.BlockWindowClosed, ex.Code);
        }
    }
}
