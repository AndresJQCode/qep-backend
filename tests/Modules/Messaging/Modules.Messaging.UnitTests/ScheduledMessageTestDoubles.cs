using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>El repositorio de programados en memoria. Hace cumplir los dos únicos de la tabla como el real (uno activo por
/// conversación, un <c>clientId</c> por conversación) y puede simular la carrera: <see cref="RaceWinner"/> es la fila que otro request
/// guardó entre la consulta por <c>clientId</c> y el <c>INSERT</c> de éste.</summary>
internal sealed class InMemoryScheduledMessageRepository : IScheduledMessageRepository
{
    public List<ScheduledMessage> Rows { get; } = [];

    /// <summary>Si se fija, el siguiente <c>AddAsync</c> guarda ésta y responde <c>already_exists</c>, como el índice.</summary>
    public ScheduledMessage? RaceWinner { get; set; }

    /// <summary>Si es <c>true</c>, el siguiente <c>AddAsync</c> responde <c>already_exists</c> sin guardar nada (otro activo, otro clientId).</summary>
    public bool FailAddAsAlreadyExists { get; set; }

    public int Adds { get; private set; }

    public Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.SingleOrDefault(row => row.TenantId == tenantId && row.ConversationId == conversationId && row.ClientId == clientId));

    public Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.SingleOrDefault(row => row.TenantId == tenantId && row.ConversationId == conversationId && row.IsActive));

    /// <summary>Lo que se confirmó con <c>CommitAsync</c> / <c>CommitDiscardAsync</c> y cuántas veces se tomó el candado.</summary>
    public List<Guid> Commits { get; } = [];

    public List<Guid> DiscardCommits { get; } = [];

    public int Locks { get; private set; }

    public Task<IScheduledMessageLock?> LockAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken)
    {
        var row = Rows.SingleOrDefault(r => r.TenantId == tenantId && r.ConversationId == conversationId && r.Id == scheduledMessageId);
        if (row is null)
        {
            return Task.FromResult<IScheduledMessageLock?>(null);
        }

        Locks++;
        return Task.FromResult<IScheduledMessageLock?>(new InMemoryLock(this, row));
    }

    private sealed class InMemoryLock(InMemoryScheduledMessageRepository owner, ScheduledMessage message) : IScheduledMessageLock
    {
        public ScheduledMessage Message => message;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.Commits.Add(message.Id);
            return Task.CompletedTask;
        }

        public Task CommitDiscardAsync(CancellationToken cancellationToken)
        {
            owner.DiscardCommits.Add(message.Id);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task AddAsync(ScheduledMessage message, CancellationToken cancellationToken)
    {
        Adds++;
        if (RaceWinner is { } winner)
        {
            RaceWinner = null;
            Rows.Add(winner);
            throw AlreadyExists();
        }

        if (FailAddAsAlreadyExists
            || Rows.Any(row => row.ConversationId == message.ConversationId && (row.ClientId == message.ClientId || (row.IsActive && message.IsActive))))
        {
            FailAddAsAlreadyExists = false;
            throw AlreadyExists();
        }

        Rows.Add(message);
        return Task.CompletedTask;
    }

    private static ResourceConflictException AlreadyExists() =>
        new(MessagingErrorCodes.ScheduledAlreadyExists, "The conversation already has a scheduled message.");
}

/// <summary>Un tenant, un miembro y los dobles de siempre más el repositorio de programados y la auditoría que anota.</summary>
internal sealed class ScheduledMessageTestBed
{
    public MessagingTestBed Bed { get; } = new();

    public InMemoryScheduledMessageRepository Scheduled { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public CreateScheduledMessageHandler CreateHandler(Guid? tenantId = null, bool canManage = true)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
        string[] permissions = canManage
            ? [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage]
            : [MessagingPermissions.ConversationRead];
        return new CreateScheduledMessageHandler(
            Scheduled,
            Bed.Conversations,
            Bed.Repository,
            Bed.Messages,
            Bed.Outbound,
            Bed.Meta,
            Bed.Store,
            Bed.Staging,
            Bed.Compressor,
            Bed.Connections,
            new FakeMemberNames(Bed.MemberId),
            membership,
            Bed.TenantModules,
            new FakeExecutionContext(tenantId ?? Bed.TenantId, Bed.SubjectId, permissions),
            new FakeClock(Bed.Now),
            Bed.Notifier,
            Bed.Receipts,
            new FakeChatbotNames(),
            Audit,
            new ScheduledMessageMapper(new FakeMemberNames(Bed.MemberId)),
            new CreateScheduledMessageValidator());
    }

    public CreateScheduledVoiceNoteHandler CreateVoiceNoteHandler(bool canManage = true)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
        string[] permissions = canManage
            ? [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage]
            : [MessagingPermissions.ConversationRead];
        return new CreateScheduledVoiceNoteHandler(
            Scheduled,
            Bed.Conversations,
            Bed.Repository,
            Bed.Messages,
            Bed.Outbound,
            Bed.Store,
            Bed.Transcoder,
            Bed.Connections,
            new FakeMemberNames(Bed.MemberId),
            membership,
            Bed.TenantModules,
            new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, permissions),
            new FakeClock(Bed.Now),
            Bed.Notifier,
            Bed.Receipts,
            new FakeChatbotNames(),
            Audit,
            new ScheduledMessageMapper(new FakeMemberNames(Bed.MemberId)),
            new CreateScheduledVoiceNoteValidator());
    }

    private (FakeMembershipDirectory Membership, FakeExecutionContext Context) ManagementContext(bool canManage, bool member)
    {
        var membership = new FakeMembershipDirectory();
        if (member)
        {
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
        }

        string[] permissions = canManage
            ? [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage]
            : [MessagingPermissions.ConversationRead];
        return (membership, new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, permissions));
    }

    private ScheduledMessageMapper Mapper => new(new FakeMemberNames(Bed.MemberId));

    public UpdateScheduledMessageHandler UpdateHandler(bool canManage = true, bool member = true)
    {
        var (membership, context) = ManagementContext(canManage, member);
        return new UpdateScheduledMessageHandler(
            Scheduled, Bed.Conversations, membership, Bed.TenantModules, context, new FakeClock(Bed.Now), Bed.Notifier, Audit, Mapper,
            new UpdateScheduledMessageValidator());
    }

    public CancelScheduledMessageHandler CancelHandler(bool canManage = true)
    {
        var (membership, context) = ManagementContext(canManage, member: true);
        return new CancelScheduledMessageHandler(
            Scheduled, Bed.Conversations, membership, Bed.TenantModules, context, new FakeClock(Bed.Now), Bed.Notifier, Audit, Bed.Store,
            new CancelScheduledMessageValidator());
    }

    public SendScheduledMessageNowHandler SendNowHandler(bool canManage = true)
    {
        var (membership, context) = ManagementContext(canManage, member: true);
        return new SendScheduledMessageNowHandler(
            Scheduled, Bed.Conversations, membership, Bed.TenantModules, context, new FakeClock(Bed.Now), Bed.Notifier, Audit, Mapper,
            new SendScheduledMessageNowValidator());
    }

    public RetryScheduledMessageHandler RetryHandler(bool canManage = true)
    {
        var (membership, context) = ManagementContext(canManage, member: true);
        return new RetryScheduledMessageHandler(
            Scheduled, Bed.Conversations, membership, Bed.TenantModules, context, new FakeClock(Bed.Now), Bed.Notifier, Audit, Mapper,
            new RetryScheduledMessageValidator());
    }

    public DiscardScheduledMessageHandler DiscardHandler(bool canManage = true)
    {
        var (membership, context) = ManagementContext(canManage, member: true);
        return new DiscardScheduledMessageHandler(
            Scheduled, Bed.Conversations, membership, Bed.TenantModules, context, new FakeClock(Bed.Now), Bed.Notifier, Audit, Bed.Store,
            new DiscardScheduledMessageValidator());
    }

    public GetScheduledMessageHandler GetHandler(Guid? tenantId = null, params string[] permissions) =>
        new(
            Scheduled,
            Bed.Conversations,
            new ScheduledMessageMapper(new FakeMemberNames(Bed.MemberId)),
            Bed.TenantModules,
            new FakeExecutionContext(tenantId ?? Bed.TenantId, Bed.SubjectId, permissions.Length > 0 ? permissions : [MessagingPermissions.ConversationRead]),
            new FakeClock(Bed.Now));
}
