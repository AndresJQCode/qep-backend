using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.3: el orden de los chequeos y la tabla de respuestas de Meta, con dobles.
/// Review Focus 2: un \0 en el texto es 422 en text.</summary>
public sealed class SendMessageHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AHappySendInsertsCallsMetaAndCommitsSentWithTheWamid()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.out", null, null);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí, tenemos 12 unidades."), Ct);

        Assert.Equal("Sent", message.Status);
        Assert.Equal(bed.ClientId, message.ClientId);
        Assert.Equal("Outbound", message.Direction);
        Assert.Equal(bed.MemberId, message.SentBy!.MemberId);
        Assert.Equal("Andrés", message.SentBy.DisplayName);
        var send = Assert.Single(bed.Meta.Sends);
        Assert.Equal(("111", conversation.WaId, "Sí, tenemos 12 unidades.", $"qep:{message.Id}"), (send.Sender.PhoneNumberId, send.To, send.Body, send.CallbackData));
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("committed-sent:wamid.out", claim.Outcome);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheExistingMessageWithoutCallingMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30); // ventana cerrada: no importa, ya salió.
        bed.Outbound.Existing = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "la primera vez");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal(bed.Outbound.Existing.Id, message.Id);
        Assert.Equal("Delivered", message.Status);
        Assert.Equal("la primera vez", message.Text);
        Assert.Empty(bed.Meta.Sends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Fact]
    public async Task AFailedMessageIsResentOnTheSameRow()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var existingId = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(existingId, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "de nuevo");
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.again", null, null);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "de nuevo"), Ct);

        Assert.Equal(existingId, message.Id);
        Assert.Equal("Sent", message.Status);
        Assert.Single(bed.Meta.Sends);
    }

    [Theory]
    [InlineData(190, MessagingErrorCodes.ConnectionUnavailable, "rolled-back", "token_expired")]
    [InlineData(133010, MessagingErrorCodes.ConnectionUnavailable, "rolled-back", "number_unregistered")]
    [InlineData(131047, MessagingErrorCodes.WindowClosed, "committed-failed:131047", null)]
    [InlineData(131026, MessagingErrorCodes.MessageRejected, "committed-failed:131026", null)]
    public async Task EveryGraphErrorHasItsCodeRowAndReport(int graphCode, string expectedCode, string expectedClaim, string? reported)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, graphCode, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedClaim, Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal(reported is null ? [] : [reported], bed.Connections.Reported.Select(report => report.FailureCode));
    }

    [Fact]
    public async Task AnUnconfirmedSendIsFailedMinusOne()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Unconfirmed, null, null, null);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal("committed-failed:-1", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Theory]
    [InlineData("resolved", MessagingErrorCodes.ConversationNotOpen)]
    [InlineData("window-closed", MessagingErrorCodes.WindowClosed)]
    [InlineData("never-wrote", MessagingErrorCodes.WindowClosed)]
    [InlineData("connection-paused", MessagingErrorCodes.ConnectionUnavailable)]
    public async Task TheChecksBeforeMetaHaveTheirCodes(string scenario, string expected)
    {
        var bed = new MessagingTestBed();
        var conversation = scenario switch
        {
            "resolved" => bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true),
            "window-closed" => bed.OpenConversation(lastInboundHoursAgo: 25),
            "never-wrote" => bed.OpenConversation(lastInboundHoursAgo: null),
            _ => bed.OpenConversation(lastInboundHoursAgo: 1),
        };
        if (scenario == "connection-paused")
        {
            bed.Connections.Sender = null;
        }

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(expected, error.Code);
        Assert.Empty(bed.Meta.Sends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData("hola\u0000", "text")]
    [InlineData("hola\u0007", "text")]
    public async Task ATextWithANullByteOrEmptyIsAValidationError(string text, string key)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, text), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == key);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task ATextWithLineBreaksAndTabsIsValid()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Hola:\r\n\t- 12 unidades"), Ct);

        Assert.Equal("Hola:\r\n\t- 12 unidades", message.Text);
    }

    [Fact]
    public async Task ATextOver4096OrAMissingClientIdIsAValidationError()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var tooLong = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, new string('x', 4097)), Ct));
        var noClient = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, null, "x"), Ct));
        var emptyClient = await Assert.ThrowsAsync<ValidationException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, Guid.Empty, "x"), Ct));

        Assert.Contains(tooLong.Errors, failure => failure.PropertyName == "text");
        Assert.Contains(noClient.Errors, failure => failure.PropertyName == "clientId");
        Assert.Contains(emptyClient.Errors, failure => failure.PropertyName == "clientId");
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAndAMissingConversationIsNotFound()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SendHandler(tenantId: Guid.CreateVersion7()).HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, Guid.CreateVersion7(), bed.ClientId, "x"), Ct));
        Assert.Equal(MessagingErrorCodes.ConversationNotFound, missing.Code);
        Assert.Empty(bed.Outbound.Claims);
    }
}
