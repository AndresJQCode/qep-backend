using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (abrir la conversación del cliente): encuentra o crea la conversación de WhatsApp del cliente en una
/// conexión, sin enviar nada; la reabre si estaba resuelta y la asigna a quien llama si no tenía asesor.</summary>
public sealed class OpenCustomerConversationHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MessagingTestBed _bed = new();
    private readonly OpenCustomerConversationFixture _fixture;

    public OpenCustomerConversationHandlerTests() => _fixture = new OpenCustomerConversationFixture(_bed);

    private OpenCustomerConversationCommand Command(Guid customerId, Guid? connectionId = null) => new(_bed.TenantId, customerId, connectionId);

    private void OneActiveWhatsApp() => _bed.Connections.Active.Add(new MessagingConnectionDto(_bed.ConnectionId, "Ventas", "111"));

    [Fact]
    public async Task WithoutConnectionAndOneActiveWhatsAppItOpensThereAndAssignsTheCaller()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.True(result.Created);
        Assert.True(result.Body.Created);
        Assert.Equal(_bed.MemberId, result.Body.Conversation.AssignedTo?.MemberId);
        Assert.Equal("Open", result.Body.Conversation.Status);
        var call = Assert.Single(_bed.Opener.Calls);
        Assert.Equal(_bed.ConnectionId, call.ConnectionId);
        Assert.Equal(_bed.MemberId, call.AssignedMemberId);
        Assert.Equal(_bed.SubjectId, call.ActorUserId);
        // La fila nueva ya llega asignada con su Taken (lo escribe el opener): el handler no agrega otro.
        Assert.Empty(_fixture.Events.Recorded);
    }

    [Fact]
    public async Task WithoutConnectionAndSeveralActiveWhatsAppItAnswersConnectionRequired()
    {
        OneActiveWhatsApp();
        _bed.Connections.Active.Add(new MessagingConnectionDto(Guid.CreateVersion7(), "Soporte", "222"));
        var contact = _bed.Customers.Add();
        // Decisión del owner 2026-11-17: aunque el cliente ya tenga hilo en una de ellas, el endpoint no elige por él.
        _bed.OpenConversation(lastInboundHoursAgo: 1, customerId: contact.CustomerId);

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(() => _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct));

        Assert.Equal("messaging.conversation.connection_required", failure.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task WithoutConnectionAndNoActiveWhatsAppItAnswersConnectionUnavailable()
    {
        var contact = _bed.Customers.Add();

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(() => _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, failure.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task OneWhatsAppPlusInstagramCountsAsOne()
    {
        OneActiveWhatsApp();
        _bed.Connections.Active.Add(new MessagingConnectionDto(Guid.CreateVersion7(), "Instagram", null, nameof(ConversationChannel.Instagram)));
        var contact = _bed.Customers.Add();

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.True(result.Created);
        Assert.Equal(_bed.ConnectionId, Assert.Single(_bed.Opener.Calls).ConnectionId);
    }

    [Fact]
    public async Task AnInstagramConnectionIsRefused()
    {
        var instagram = Guid.CreateVersion7();
        _bed.Connections.Channels[instagram] = ConversationChannel.Instagram;

        // Sin cliente sembrado: si el handler mirara el cliente antes, respondería 404 y no channel_unsupported.
        var failure = await Assert.ThrowsAsync<MessagingDomainException>(
            () => _fixture.Handler().HandleAsync(Command(Guid.CreateVersion7(), instagram), Ct));

        Assert.Equal(MessagingErrorCodes.ChannelUnsupported, failure.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task APausedConnectionPassedExplicitlyIsUnavailable()
    {
        _bed.Connections.Sender = null;
        var contact = _bed.Customers.Add();

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(
            () => _fixture.Handler().HandleAsync(Command(contact.CustomerId, _bed.ConnectionId), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, failure.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task AnInactiveOrMissingCustomerIs404()
    {
        OneActiveWhatsApp();
        var inactive = _bed.Customers.Add(active: false);

        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _fixture.Handler().HandleAsync(Command(Guid.CreateVersion7()), Ct));
        var dropped = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _fixture.Handler().HandleAsync(Command(inactive.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.CustomerNotFound, missing.Code);
        Assert.Equal(MessagingErrorCodes.CustomerNotFound, dropped.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task ACustomerWithoutPhoneOrBsuidIs422Unreachable()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add(phone: null, userId: null);

        var failure = await Assert.ThrowsAsync<MessagingDomainException>(() => _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.CustomerUnreachable, failure.Code);
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task WithoutCustomerIdItFailsValidation()
    {
        OneActiveWhatsApp();

        var failure = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => _fixture.Handler().HandleAsync(new OpenCustomerConversationCommand(_bed.TenantId, null, null), Ct));

        Assert.Contains(failure.Errors, error => error.PropertyName == "customerId");
        Assert.Empty(_bed.Opener.Calls);
    }

    [Fact]
    public async Task AnExistingUnassignedConversationIsTakenByTheCaller()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();
        var row = _bed.OpenConversation(lastInboundHoursAgo: 1, customerId: contact.CustomerId);

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.False(result.Created);
        Assert.Equal(row.Id, result.Body.Conversation.Id);
        Assert.Equal(_bed.MemberId, result.Body.Conversation.AssignedTo?.MemberId);
        Assert.Equal([ConversationEventType.Taken], _fixture.Events.Recorded.Select(change => change.Type));
        Assert.Equal([MessagingAuditActions.Taken], _fixture.Audit.Actions);
        Assert.Equal(1, _fixture.UnitOfWork.Saves);
        Assert.Single(_bed.Notifier.Events);
    }

    [Fact]
    public async Task AnExistingResolvedConversationIsReopenedThenTaken()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();
        _bed.OpenConversation(lastInboundHoursAgo: 30, resolved: true, customerId: contact.CustomerId);

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.False(result.Created);
        Assert.Equal("Open", result.Body.Conversation.Status);
        Assert.Equal(_bed.MemberId, result.Body.Conversation.AssignedTo?.MemberId);
        Assert.Equal([ConversationEventType.Reopened, ConversationEventType.Taken], _fixture.Events.Recorded.Select(change => change.Type));
        Assert.Equal(_bed.MemberId, _fixture.Events.Recorded[0].Actor);
        Assert.Equal([MessagingAuditActions.Reopened, MessagingAuditActions.Taken], _fixture.Audit.Actions);
        Assert.Equal(1, _fixture.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConversationAssignedToSomeoneElseIsLeftAssigned(bool resolved)
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();
        var other = Guid.CreateVersion7();
        _bed.OpenConversation(lastInboundHoursAgo: 1, resolved: resolved, assignedTo: other, customerId: contact.CustomerId);

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.Equal(other, _fixture.AssignedOf(result.Body.Conversation.Id));
        Assert.NotEqual(true, result.Body.Conversation.AssignedTo?.IsMe);
        Assert.Equal("Open", result.Body.Conversation.Status);
        Assert.DoesNotContain(_fixture.Events.Recorded, change => change.Type == ConversationEventType.Taken);
        Assert.Equal(resolved ? [ConversationEventType.Reopened] : [], _fixture.Events.Recorded.Select(change => change.Type));
    }

    [Fact]
    public async Task AConversationAlreadyMineAndOpenRecordsNothing()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();
        var row = _bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: _bed.MemberId, customerId: contact.CustomerId);
        var version = _fixture.VersionOf(row.Id);

        var result = await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.False(result.Created);
        Assert.Empty(_fixture.Events.Recorded);
        Assert.Empty(_fixture.Audit.Actions);
        Assert.Equal(0, _fixture.UnitOfWork.Saves);
        Assert.Empty(_bed.Notifier.Events);
        Assert.Equal(version, _fixture.VersionOf(row.Id));
        Assert.Equal(version, result.Body.Conversation.Version);
    }

    [Fact]
    public async Task TakingAnUnassignedConversationWithABotPausesIt()
    {
        OneActiveWhatsApp();
        var contact = _bed.Customers.Add();
        _bed.OpenConversation(lastInboundHoursAgo: 1, customerId: contact.CustomerId, automation: ConversationAutomationStatus.Active);

        await _fixture.Handler().HandleAsync(Command(contact.CustomerId), Ct);

        Assert.Equal(
            [ConversationEventType.Taken, ConversationEventType.AutomationPaused],
            _fixture.Events.Recorded.Select(change => change.Type));
        // Como «Tomar conversación»: la corrida viva del bot recibe la señal de soltarla en el mismo guardado.
        Assert.Equal([BotKind.Chatbot], _fixture.Inbox.Releases);
    }
}

/// <summary>Arma el handler sobre el banco: el agregado se materializa desde la fila del banco y el guardado proyecta el agregado de
/// vuelta a la fila, así el resumen que devuelve el handler se lee como en producción (después del commit).</summary>
internal sealed class OpenCustomerConversationFixture(MessagingTestBed bed)
{
    public RecordingConversationEvents Events { get; } = new();

    public RecordingConversationAudit Audit { get; } = new();

    public ReleaseSignals Inbox { get; } = new();

    public AggregateConversationRepository Repository { get; } = new(bed);

    public ProjectingUnitOfWork UnitOfWork => _unitOfWork ??= new ProjectingUnitOfWork(bed, Repository);

    private ProjectingUnitOfWork? _unitOfWork;

    public long VersionOf(Guid conversationId) => Repository.Materialize(conversationId).Version;

    public Guid? AssignedOf(Guid conversationId) => Repository.Materialize(conversationId).AssignedMemberId;

    public OpenCustomerConversationHandler Handler()
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(bed.SubjectId, bed.TenantId)] = bed.MemberId;
        var memberNames = new FakeMemberNames(bed.MemberId);
        var context = new FakeExecutionContext(bed.TenantId, bed.SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage);
        var caller = new CallerMembership(membership, context);
        var summaries = new ConversationSummaryBuilder(
            bed.Connections, bed.Customers, memberNames, new FakeResolutionReasonNames(), caller, new FakeChatbotNames(), new FakeAutomationManualStart());
        var steps = new ConversationAssignmentSteps(
            Repository, bed.Conversations, UnitOfWork, Audit, Events, summaries, caller, bed.TenantModules, context, new FakeClock(bed.Now), bed.Notifier);
        return new OpenCustomerConversationHandler(
            bed.Connections, bed.Customers, bed.Opener, Repository, steps, caller, bed.TenantModules, context, Inbox, new NoBotDelegations(),
            new OpenCustomerConversationValidator());
    }
}

/// <summary>El agregado de cada fila del banco, armado con las transiciones del dominio (asignar, arrancar el bot, resolver) y una
/// sola vez por id, como el tracker de EF. La versión de la fila se alinea con la del agregado.</summary>
internal sealed class AggregateConversationRepository(MessagingTestBed bed) : IConversationRepository
{
    private readonly Dictionary<Guid, Conversation> _tracked = [];

    public IEnumerable<Conversation> Tracked => _tracked.Values;

    public Conversation Materialize(Guid conversationId)
    {
        if (_tracked.TryGetValue(conversationId, out var tracked))
        {
            return tracked;
        }

        var index = bed.Conversations.Rows.FindIndex(row => row.Id == conversationId);
        var row = bed.Conversations.Rows[index];
        var at = row.UpdatedAt.AddDays(-1);
        var conversation = Conversation.Start(row.Id, row.TenantId, row.ConnectionId, row.WaId ?? "573001234567", row.ProfileName, at);
        if (row.AssignedMemberId is { } assigned)
        {
            conversation.Take(assigned, at);
        }

        if (row.AutomationStatus == ConversationAutomationStatus.Active)
        {
            conversation.StartAutomation(Guid.CreateVersion7(), at);
        }

        if (row.Status == ConversationStatus.Resolved)
        {
            conversation.Resolve(at);
        }

        bed.Conversations.Rows[index] = row with { Version = conversation.Version };
        _tracked[conversationId] = conversation;
        return conversation;
    }

    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(bed.Conversations.Rows.Any(row => row.TenantId == tenantId && row.Id == conversationId) ? Materialize(conversationId) : null);

    public Task<AutoAssignOutcome> TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RepliedTarget?> MarkRepliedAsync(Guid tenantId, Guid conversationId, int seen, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> MarkUnreadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UnreadFromOutcome> MarkUnreadFromAsync(Guid tenantId, Guid conversationId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> ExistsAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> SetAutomationStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> SetAutomationChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Guid?> LinkCustomerAsync(Guid tenantId, Guid conversationId, Guid customerId, bool created, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El guardado escribe en las filas del banco lo que cambió el agregado.</summary>
internal sealed class ProjectingUnitOfWork(MessagingTestBed bed, AggregateConversationRepository repository) : IMessagingUnitOfWork
{
    public int Saves { get; private set; }

    public void DiscardPendingChanges() => throw new NotSupportedException();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        foreach (var conversation in repository.Tracked)
        {
            var index = bed.Conversations.Rows.FindIndex(row => row.Id == conversation.Id);
            bed.Conversations.Rows[index] = bed.Conversations.Rows[index] with
            {
                Status = conversation.Status,
                AssignedMemberId = conversation.AssignedMemberId,
                AutomationStatus = conversation.AutomationStatus,
                Version = conversation.Version,
            };
        }

        return Task.FromResult(1);
    }

    public Task<IMessagingTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class RecordingConversationEvents : IConversationEvents
{
    public List<ConversationEvent> Recorded { get; } = [];

    public void Record(Guid tenantId, Guid connectionId, Guid conversationId, ConversationEvent value, DateTimeOffset occurredAt) => Recorded.Add(value);
}

internal sealed class RecordingConversationAudit : IMessagingAuditRecorder
{
    public List<string> Actions { get; } = [];

    public void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt) => Actions.Add(action);

    public void RecordBySystem(Guid tenantId, string action, Guid conversationId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordCampaign(Guid tenantId, Guid actorId, bool bySystem, string action, Guid campaignId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordQuickReplyOrder(Guid tenantId, Guid actorId, Guid memberId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordBusinessHours(Guid tenantId, Guid actorId, string action, Guid scheduleId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordTemplate(
        Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt, IReadOnlyCollection<string>? changedFields = null) =>
        throw new NotSupportedException();

    public void RecordHistorySync(
        Guid tenantId, Guid actorId, string action, Guid connectionId, DateTimeOffset occurredAt, IReadOnlyCollection<string> retriedTypes) =>
        throw new NotSupportedException();

    public void RecordScheduledMessage(
        Guid tenantId, Guid actorId, bool bySystem, string action, Guid scheduledMessageId, DateTimeOffset occurredAt,
        IReadOnlyCollection<string>? changedFields = null) =>
        throw new NotSupportedException();
}

internal sealed class NoBotDelegations : IBotDelegationRepository
{
    public Task<BotDelegation?> FindAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken) => Task.FromResult<BotDelegation?>(null);

    public void Add(BotDelegation delegation) => throw new NotSupportedException();

    public Task<int> InterruptLiveAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Sólo la señal de soltar la corrida (lo que encola tomar una conversación con bot).</summary>
internal sealed class ReleaseSignals : IAutomationInbox
{
    public List<BotKind> Releases { get; } = [];

    public void EnqueueReleaseRun(Guid tenantId, Guid connectionId, Guid conversationId, BotKind botKind, DateTimeOffset now) => Releases.Add(botKind);

    public Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, BotKind botKind, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Guid?> FindLastInboundIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void EnqueueManualStart(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now) => throw new NotSupportedException();

    public Task<bool> HasPendingManualStartAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void EnqueueDelegated(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now) =>
        throw new NotSupportedException();

    public void EnqueueAgentReturned(Guid tenantId, Guid connectionId, Guid conversationId, Guid delegationId, DateTimeOffset now) =>
        throw new NotSupportedException();

    public Task<DateTimeOffset?> ReassignPendingEngineAsync(
        Guid tenantId, Guid conversationId, BotKind target, Guid? exceptMessageId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
