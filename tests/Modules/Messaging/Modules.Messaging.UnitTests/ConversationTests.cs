using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.4–§8.5 y §8.7: resolver/reabrir con sus 422, cuándo procede el acuse de leído, y la ventana
/// de 24 h desde el último mensaje de la persona.</summary>
public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static Conversation Open() =>
        Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);

    [Fact]
    public void StartIsOpenWithoutUnreadNorWindow()
    {
        var conversation = Open();

        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(0, conversation.UnreadCount);
        Assert.Null(conversation.LastInboundAt);
        Assert.Null(conversation.CustomerWindowExpiresAt);
        Assert.False(conversation.IsWindowOpen(Now));
        Assert.Equal(1, conversation.Version);
        Assert.Equal("573001234567", conversation.WaId);
    }

    [Fact]
    public void ResolveAndReopenBumpTheVersionAndRejectANoOp()
    {
        var conversation = Open();

        conversation.Resolve(Now.AddMinutes(1));
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);
        Assert.Equal(2, conversation.Version);
        Assert.Equal(Now.AddMinutes(1), conversation.UpdatedAt);

        var again = Assert.Throws<MessagingDomainException>(() => conversation.Resolve(Now.AddMinutes(2)));
        Assert.Equal(MessagingErrorCodes.AlreadyResolved, again.Code);
        Assert.Equal(2, conversation.Version);

        conversation.Reopen(Now.AddMinutes(3));
        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(3, conversation.Version);
        var open = Assert.Throws<MessagingDomainException>(() => conversation.Reopen(Now.AddMinutes(4)));
        Assert.Equal(MessagingErrorCodes.AlreadyOpen, open.Code);
    }

    // §8.4: marcar leído es un UPDATE atómico en Infrastructure; el dominio sólo decide si el acuse a Meta
    // procede con lo que ese UPDATE devuelve (wamid presente y último entrante de menos de 30 días).
    [Theory]
    [InlineData("wamid.in", -1.0, true)]
    [InlineData("wamid.in", -29.9, true)]
    [InlineData("wamid.in", -30.0, false)]
    [InlineData(null, -1.0, false)]
    public void TheReadReceiptNeedsAWamidAndAnInboundYoungerThanThirtyDays(string? wamid, double daysAgo, bool expected) =>
        Assert.Equal(expected, Conversation.CanAcknowledgeReading(wamid, Now.AddDays(daysAgo), Now));

    [Fact]
    public void TheReadReceiptNeedsAnInboundDate() =>
        Assert.False(Conversation.CanAcknowledgeReading("wamid.in", null, Now));

    [Theory]
    [InlineData(-23, true)]
    [InlineData(-24, false)]
    [InlineData(-25, false)]
    public void TheWindowIsTwentyFourHoursFromTheLastInbound(int hoursAgo, bool open)
    {
        var conversation = Conversation.ForTests(Open(), unreadCount: 0, lastInboundAt: Now.AddHours(hoursAgo));

        Assert.Equal(Now.AddHours(hoursAgo).AddHours(24), conversation.CustomerWindowExpiresAt);
        Assert.Equal(open, conversation.IsWindowOpen(Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+573001234567")]
    public void TheWaIdIsDigitsOnly(string? waId) =>
        Assert.Throws<ArgumentException>(() => Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), waId!, null, Now));

    // Spec 2026-10-10 §6.1.1: ISO alfa-2 en mayúsculas + «.» + 1 a 128 alfanuméricos.
    [Theory]
    [InlineData("CO.1349120865530274", true)]
    [InlineData("US.13491208655302741918", true)]
    [InlineData("US.abcXYZ09", true)]
    [InlineData("co.1349", false)]
    [InlineData("COL.1349", false)]
    [InlineData("CO.", false)]
    [InlineData("CO.13-49", false)]
    [InlineData("573001234567", false)]
    [InlineData("CO.1349\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABsuidHasTheShapeMetaDocuments(string? value, bool expected) =>
        Assert.Equal(expected, Conversation.IsValidUserId(value));

    [Fact]
    public void ABsuidOfMoreThan128AlphanumericsIsRejected()
    {
        Assert.True(Conversation.IsValidUserId("CO." + new string('9', 128)));
        Assert.False(Conversation.IsValidUserId("CO." + new string('9', 129)));
    }

    [Fact]
    public void ABsuidConversationMayHaveNoPhone()
    {
        var conversation = Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "CO.1349120865530274", null, "Laura", Now);

        Assert.Equal("CO.1349120865530274", conversation.UserId);
        Assert.Null(conversation.WaId);
        Assert.Null(conversation.AssignedMemberId);
        Assert.Null(conversation.CustomerId);
        Assert.Equal(1, conversation.Version);
    }

    [Theory]
    [InlineData("573001234567", "57300123456X")]
    [InlineData("co.1349", null)]
    public void AStartWithAMalformedIdentityIsRejected(string userId, string? waId) =>
        Assert.Throws<ArgumentException>(() =>
            Conversation.StartWithUserId(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), userId, waId, null, Now));
}
