using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>El indicador de escritura del asesor: sólo sale si el asesor podría contestar ya mismo, y siempre es best effort.</summary>
public sealed class SendTypingIndicatorHandlerTests
{
    private const string Wamid = "wamid.in";

    private const string Igsid = "1234567890123456";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<bool> TypeAsync(MessagingTestBed bed, ConversationRow conversation, SendTypingIndicatorHandler? handler = null) =>
        (handler ?? bed.TypingHandler()).HandleAsync(new SendTypingIndicatorCommand(bed.TenantId, conversation.Id), Ct);

    private static void AssertNothingWentToMeta(MessagingTestBed bed)
    {
        Assert.Empty(bed.Meta.Typing);
        Assert.Empty(bed.Instagram.Typing);
        Assert.Empty(bed.Meta.ReadReceipts);
        Assert.Empty(bed.Instagram.Seen);
    }

    [Fact]
    public async Task WhatsAppGetsTheIndicatorOnTheLastInbound()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, lastInboundWamid: Wamid);

        Assert.True(await TypeAsync(bed, conversation));

        Assert.Equal(Wamid, Assert.Single(bed.Meta.Typing));
        Assert.Empty(bed.Instagram.Typing);
        Assert.Empty(bed.Meta.ReadReceipts);
    }

    [Fact]
    public async Task InstagramGetsTheIndicatorOnThePerson()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, userId: Igsid, waId: null, channel: ConversationChannel.Instagram, lastInboundWamid: "mid.in");

        Assert.True(await TypeAsync(bed, conversation));

        Assert.Equal(Igsid, Assert.Single(bed.Instagram.Typing).RecipientId);
        Assert.Empty(bed.Meta.Typing);
        Assert.Empty(bed.Instagram.Seen);
    }

    [Fact]
    public async Task AConversationAssignedToTheCallerGetsTheIndicator()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, assignedTo: bed.MemberId, lastInboundWamid: Wamid);

        Assert.True(await TypeAsync(bed, conversation));

        Assert.Single(bed.Meta.Typing);
    }

    [Fact]
    public async Task APausedAssistantDoesNotStopTheIndicator()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid, automation: ConversationAutomationStatus.Paused);

        Assert.True(await TypeAsync(bed, conversation));

        Assert.Single(bed.Meta.Typing);
    }

    [Fact]
    public async Task AResolvedConversationIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, resolved: true, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task ABlockedContactIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, blocked: true, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AConversationAssignedToSomeoneElseIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, assignedTo: Guid.CreateVersion7(), lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public async Task AClosedWindowIsANoOp(int hoursAgo)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(hoursAgo, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AConversationWithoutInboundIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: null);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnActiveAssistantIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid, automation: ConversationAutomationStatus.Active);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnInboundWithoutWamidIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: null);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnInstagramConversationWithoutIgsidIsANoOp()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, userId: null, waId: null, channel: ConversationChannel.Instagram, lastInboundWamid: "mid.in");

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnUnavailableConnectionIsANoOp()
    {
        var bed = new MessagingTestBed();
        bed.Connections.Sender = null;
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnUnavailableInstagramConnectionIsANoOp()
    {
        var bed = new MessagingTestBed();
        bed.Connections.InstagramSender = null;
        var conversation = bed.OpenConversation(1, userId: Igsid, waId: null, channel: ConversationChannel.Instagram, lastInboundWamid: "mid.in");

        Assert.False(await TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task AnUnknownConversationIs404()
    {
        var bed = new MessagingTestBed();
        var missing = new SendTypingIndicatorCommand(bed.TenantId, Guid.CreateVersion7());

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.TypingHandler().HandleAsync(missing, Ct));
    }

    [Fact]
    public async Task AnotherTenantsConversationIs404()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);
        var other = Guid.CreateVersion7();
        var command = new SendTypingIndicatorCommand(other, conversation.Id);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.TypingHandler(tenantId: other).HandleAsync(command, Ct));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task ACallerFromAnotherTenantIs403()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => TypeAsync(bed, conversation, bed.TypingHandler(tenantId: Guid.CreateVersion7())));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task WithoutTheManagePermissionIs403()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        await Assert.ThrowsAsync<RequestForbiddenException>(
            () => TypeAsync(bed, conversation, bed.TypingHandler(permissions: MessagingPermissions.ConversationRead)));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task WithoutTheMessagingModuleIs403()
    {
        var bed = new MessagingTestBed();
        bed.TenantModules.Sets[bed.TenantId] = TenantModuleSet.Empty;
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => TypeAsync(bed, conversation));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task WithoutAnActiveMembershipIs403()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => TypeAsync(bed, conversation, bed.TypingHandler(member: false)));

        AssertNothingWentToMeta(bed);
    }

    [Fact]
    public async Task ARejectedWhatsAppIndicatorIsLoggedWithoutIdentifiers()
    {
        var bed = new MessagingTestBed();
        bed.Meta.TypingSucceeds = false;
        var logger = new RecordingLogger<SendTypingIndicatorHandler>();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation, bed.TypingHandler(logger)));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(conversation.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Wamid, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARejectedInstagramIndicatorIsLoggedWithoutIdentifiers()
    {
        var bed = new MessagingTestBed();
        bed.Instagram.TypingSucceeds = false;
        var logger = new RecordingLogger<SendTypingIndicatorHandler>();
        var conversation = bed.OpenConversation(1, userId: Igsid, waId: null, channel: ConversationChannel.Instagram, lastInboundWamid: "mid.in");

        Assert.False(await TypeAsync(bed, conversation, bed.TypingHandler(logger)));

        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain(Igsid, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mid.in", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExceptionFromMetaIsSwallowedAndLogged()
    {
        var bed = new MessagingTestBed();
        bed.Meta.TypingException = new HttpRequestException("meta caída");
        var logger = new RecordingLogger<SendTypingIndicatorHandler>();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        Assert.False(await TypeAsync(bed, conversation, bed.TypingHandler(logger)));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task AnExceptionFromInstagramIsSwallowedAndLogged()
    {
        var bed = new MessagingTestBed();
        bed.Instagram.TypingException = new HttpRequestException("meta caída");
        var logger = new RecordingLogger<SendTypingIndicatorHandler>();
        var conversation = bed.OpenConversation(1, userId: Igsid, waId: null, channel: ConversationChannel.Instagram, lastInboundWamid: "mid.in");

        Assert.False(await TypeAsync(bed, conversation, bed.TypingHandler(logger)));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task ACancellationIsNotSwallowed()
    {
        var bed = new MessagingTestBed();
        bed.Meta.TypingException = new OperationCanceledException();
        var conversation = bed.OpenConversation(1, lastInboundWamid: Wamid);

        await Assert.ThrowsAsync<OperationCanceledException>(() => TypeAsync(bed, conversation));
    }
}
