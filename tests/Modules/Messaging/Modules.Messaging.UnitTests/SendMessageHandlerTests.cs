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
        // El «!»: la fila del banco de pruebas siempre tiene teléfono (OpenConversation).
        Assert.Equal(
            ("111", (SendTarget)new SendToPhone(conversation.WaId!), "Sí, tenemos 12 unidades.", $"qep:{message.Id}"),
            (send.Sender.PhoneNumberId, send.Target, send.Body, send.CallbackData));
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("committed-sent:wamid.out", claim.Outcome);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheExistingMessageWithoutCallingMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30); // ventana cerrada: no importa, ya salió.
        bed.Outbound.Committed = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "la primera vez");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal(bed.Outbound.Committed.Id, message.Id);
        Assert.Equal("Delivered", message.Status);
        Assert.Equal("la primera vez", message.Text);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    // Dos requests en vuelo con el mismo clientId: al segundo lo resuelve el reclamo (FOR UPDATE), no el paso 2.
    [Fact]
    public async Task AnInFlightClientIdIsResolvedByTheClaimWithoutCallingMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Outbound.Existing = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-1), bed.MemberId, "la primera vez");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal((bed.Outbound.Existing.Id, "la primera vez"), (message.Id, message.Text));
        Assert.Empty(bed.Meta.Sends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    // RF6: asignada a otra persona → 422 sin reclamo y sin Meta.
    [Fact]
    public async Task AnswerToSomeoneElsesConversationIs422WithoutClaimNorMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    [Fact]
    public async Task AnUnassignedConversationIsTakenBeforeTheClaim()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct);

        Assert.Equal([conversation.Id], bed.Repository.AutoAssigned);
        Assert.Single(bed.Meta.Sends);
    }

    [Fact]
    public async Task MyOwnConversationIsNotTakenAgain()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct);

        Assert.Empty(bed.Repository.AutoAssigned);
        Assert.Single(bed.Meta.Sends);
    }

    // §9.3: el take ganó entre la lectura y el UPDATE condicional.
    [Fact]
    public async Task LosingTheAutoTakeRaceIs422WithoutMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Repository.NextAutoAssign = AutoAssignOutcome.AssignedToOther;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Outbound.Claims);
    }

    // Un reintento que leyó la conversación sin dueño justo antes de que su primera vuelta la tomara: el UPDATE
    // condicional responde AlreadyMine y el envío sigue, sin otro AutoTaken.
    [Fact]
    public async Task ARetryThatFindsItAlreadyMineSends()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Repository.NextAutoAssign = AutoAssignOutcome.AlreadyMine;

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "hola"), Ct);

        Assert.Single(bed.Meta.Sends);
    }

    // §8.5 paso 2: lo que ya salió, ya salió, aunque ahora la tenga otra persona o la ventana esté cerrada.
    [Fact]
    public async Task AnAlreadySentClientIdIsReturnedEvenIfSomeoneElseTookTheConversation()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 30, assignedTo: Guid.CreateVersion7());
        bed.Outbound.Committed = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-5), bed.MemberId, "ya salió");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "otra vez"), Ct);

        Assert.Equal((bed.Outbound.Committed.Id, "ya salió"), (message.Id, message.Text));
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task AQuotedReplySendsTheContextAndReturnsTheReplyTo()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var quoted = Guid.CreateVersion7();
        bed.Messages.Targets[quoted] = new ReplyTargetRow(quoted, conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí", quoted), Ct);

        Assert.Equal("wamid.q", Assert.Single(bed.Meta.Sends).ContextWamid);
        Assert.Equal(new ReplyToDto(quoted, "Inbound", "Text", "¿Tienen?"), message.ReplyTo);
        Assert.Equal((quoted, "wamid.q"), (Assert.Single(bed.Outbound.Claims).Draft.ReplyToMessageId!.Value, bed.Outbound.Claims[0].Draft.ReplyToWamid));
    }

    // §5.1: repetir un clientId devuelve la cita guardada, aunque el cuerpo traiga otra.
    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredReplyTo()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var stored = Guid.CreateVersion7();
        bed.Messages.Targets[stored] = new ReplyTargetRow(stored, conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");
        bed.Outbound.Committed = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-5), bed.MemberId, "Sí", stored);

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí", Guid.CreateVersion7()), Ct);

        Assert.Equal(stored, message.ReplyTo!.Id);
        Assert.Empty(bed.Meta.Sends);
    }

    [Theory]
    [InlineData("other-conversation")]
    [InlineData("no-wamid")]
    [InlineData("reaction")]
    [InlineData("event")]
    [InlineData("unknown")]
    public async Task AReplyToThatCannotBeQuotedIsAValidationErrorOnReplyTo(string shape)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var quoted = Guid.CreateVersion7();
        if (shape != "unknown")
        {
            bed.Messages.Targets[quoted] = new ReplyTargetRow(
                quoted,
                shape == "other-conversation" ? Guid.CreateVersion7() : conversation.Id,
                shape == "event" ? MessageDirection.System : MessageDirection.Inbound,
                shape switch { "reaction" => MessageKind.Reaction, "event" => MessageKind.Event, _ => MessageKind.Text },
                "x",
                null,
                shape is "no-wamid" or "event" ? null : "wamid.q");
        }

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Sí", quoted), Ct));

        Assert.Equal("replyTo", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Meta.Sends);
        Assert.Empty(bed.Outbound.Claims);
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

    /// <summary>Fix 1, hallazgo 2: la persona cierra la pestaña mientras Meta acepta el mensaje. El envío ya salió:
    /// la fila tiene que quedar Sent, o el reintento con el mismo clientId lo mandaría dos veces.</summary>
    [Fact]
    public async Task AnAbortedRequestAfterMetaAcceptedStillCommitsSentAndARetryDoesNotResend()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.aborted", null, null);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bed.Meta.DuringSend = request.Cancel;

        var message = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), request.Token);

        Assert.Equal("Sent", message.Status);
        Assert.Equal("committed-sent:wamid.aborted", Assert.Single(bed.Outbound.Claims).Outcome);

        bed.Meta.DuringSend = null;
        bed.Outbound.Existing = new ExistingOutbound(message.Id, MessageStatus.Sent, message.At, bed.MemberId, "x");
        var retry = await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct);

        Assert.Equal(message.Id, retry.Id);
        Assert.Single(bed.Meta.Sends);
    }

    [Fact]
    public async Task AnAbortedRequestOnAGraphErrorStillRollsBackAndReports()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, 190, "title");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bed.Meta.DuringSend = request.Cancel;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), request.Token));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal("token_expired", Assert.Single(bed.Connections.Reported).FailureCode);
    }

    /// <summary>Hallazgo 5: si Integrations no pudo pasar la conexión a NeedsAttention, el envío sigue siendo el 422
    /// documentado, con la falla original adentro para que quede en <c>platform.request_failures</c>.</summary>
    [Fact]
    public async Task AFailedHealthReportStillAnswersConnectionUnavailableWithTheCause()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, 133010, "title");
        var cause = new InvalidOperationException("integrations down");
        bed.Connections.ReportFailure = cause;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Same(cause, error.InnerException);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    /// <summary>Hallazgo 4: un 4xx sin <c>error.code</c> legible se guarda con <c>failure_code = 0</c>, que
    /// <c>MessageFailureReasons</c> traduce al texto genérico.</summary>
    [Fact]
    public async Task AGraphErrorWithoutACodeIsFailedZeroWithTheGenericReason()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, null, null);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "x"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal("committed-failed:0", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal(MessageFailureReasons.Generic, MessageFailureReasons.For(SendMessageHandler.UnknownGraphErrorCode));
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
        // Spec 2026-10-10 §8.5: status y ventana se miran antes del reclamo; la conexión, ya con el reclamo tomado.
        Assert.Equal(scenario == "connection-paused" ? ["rolled-back"] : [], bed.Outbound.Claims.Select(claim => claim.Outcome));
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
