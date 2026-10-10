using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Dobles en memoria para los handlers de Messaging (Task 16): un tenant, un miembro, una conexión
/// <c>Active</c> con el número <c>111</c> y un reloj fijo. Las copias de los dobles de Integrations no se
/// comparten: Messaging no referencia sus pruebas.</summary>
internal sealed class MessagingTestBed
{
    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public Guid MemberId { get; } = Guid.CreateVersion7();

    public Guid ConnectionId { get; } = Guid.CreateVersion7();

    public Guid ClientId { get; } = Guid.CreateVersion7();

    public DateTimeOffset Now { get; } = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    public InMemoryConversationQueries Conversations { get; } = new();

    public FakeConnectionDirectory Connections { get; } = new();

    public FakeWhatsAppClient Meta { get; } = new();

    public FakeOutboundMessages Outbound { get; } = new();

    public FakeTenantModules TenantModules { get; } = new();

    public FakeConversationRepository Repository { get; } = new();

    public FakeMessageQueries Messages { get; } = new();

    public ConversationRow OpenConversation(int? lastInboundHoursAgo, bool resolved = false, Guid? assignedTo = null)
    {
        var row = new ConversationRow(
            Guid.CreateVersion7(), TenantId, ConnectionId, UserId: null, WaId: "573001234567", Username: null, "Laura",
            CustomerId: null, AssignedMemberId: assignedTo, resolved ? ConversationStatus.Resolved : ConversationStatus.Open, 0,
            lastInboundHoursAgo is { } hours ? Now.AddHours(-hours) : null,
            null, null, null, null, null, null, Now, 1);
        Conversations.Rows.Add(row);
        return row;
    }

    public SendMessageHandler SendHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        return new SendMessageHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Meta,
            Connections,
            new FakeMemberNames(MemberId),
            membership,
            TenantModules,
            new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(Now),
            new SendMessageValidator());
    }
}

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

/// <summary>Sin entrada = tenant que no está en <c>tenancy.tenants</c> (el stub): <c>null</c>, todo visible.</summary>
internal sealed class FakeTenantModules : ITenantModules
{
    public Dictionary<Guid, TenantModuleSet> Sets { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantModuleSet?>(Sets.TryGetValue(tenantId, out var set) ? set : null);
}

internal sealed class InMemoryConversationQueries : IConversationQueries
{
    public List<ConversationRow> Rows { get; } = [];

    public Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(
        Guid tenantId, ConversationListFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var items = Rows
            .Where(row => row.TenantId == tenantId && row.Status == filter.Status)
            .Where(row => filter.Assigned switch
            {
                AssignedFilter.Mine => filter.MemberId is { } me && row.AssignedMemberId == me,
                AssignedFilter.Unassigned => row.AssignedMemberId is null,
                _ => true,
            })
            .ToList();
        return Task.FromResult<(IReadOnlyList<ConversationRow>, int)>((items, items.Count));
    }

    public Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken)
    {
        var open = Rows.Where(row => row.TenantId == tenantId && row.Status == ConversationStatus.Open).ToList();
        return Task.FromResult(new ConversationCountsDto(
            open.Count,
            Rows.Where(row => row.TenantId == tenantId).Sum(row => row.UnreadCount),
            memberId is { } me ? open.Count(row => row.AssignedMemberId == me) : 0,
            open.Count(row => row.AssignedMemberId is null)));
    }

    public Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.SingleOrDefault(row => row.TenantId == tenantId && row.Id == conversationId));

    public Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationRow>>(Rows.Where(row => row.TenantId == tenantId && ids.Contains(row.Id)).ToList());
}

internal sealed record RejectedReport(Guid TenantId, Guid ConnectionId, string FailureCode);

internal sealed class FakeConnectionDirectory : IMessagingConnectionDirectory
{
    public MessagingSender? Sender { get; set; } = new("111", "token");

    public List<RejectedReport> Reported { get; } = [];

    public Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        Task.FromResult<MessagingRoute?>(null);

    public Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessagingRoute>>([]);

    public Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sender);

    public Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    /// <summary>Simula que Integrations no pudo guardar el cambio de estado.</summary>
    public Exception? ReportFailure { get; set; }

    public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReportFailure is { } failure)
        {
            throw failure;
        }

        Reported.Add(new RejectedReport(tenantId, connectionId, failureCode));
        return Task.CompletedTask;
    }
}

internal sealed record SentText(MessagingSender Sender, SendTarget Target, string Body, string CallbackData, string? ContextWamid);

internal sealed class FakeWhatsAppClient : IWhatsAppCloudClient
{
    public SendTextResult NextSend { get; set; } = new(SendOutcome.Sent, "wamid.default", null, null);

    public List<SentText> Sends { get; } = [];

    /// <summary>Corre mientras "Meta" procesa: p. ej., la persona cierra la pestaña (se cancela el request).</summary>
    public Action? DuringSend { get; set; }

    /// <summary>Como HttpClient: Meta ya recibió el mensaje, pero un token cancelado a mitad de la llamada lanza.</summary>
    public Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        Sends.Add(new SentText(sender, target, body, callbackData, contextWamid));
        DuringSend?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextSend);
    }

    public Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>§8.6: lo que responde <c>GET /{media-id}</c>; sin configurar, la prueba no debía llegar acá.</summary>
    public MessagingGraphResult<MediaInfo>? MediaInfo { get; set; }

    /// <summary>§8.6: el cuerpo de la URL firmada, o la excepción que lanza.</summary>
    public Func<Uri, CancellationToken, Stream>? OpenMedia { get; set; }

    public List<string> MediaRequests { get; } = [];

    public Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken)
    {
        MediaRequests.Add(mediaId);
        return Task.FromResult(MediaInfo ?? throw new NotSupportedException());
    }

    public Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken) =>
        Task.FromResult((OpenMedia ?? throw new NotSupportedException())(url, cancellationToken));
}

/// <summary>Cada <c>ClaimAsync</c> deja un <see cref="FakeClaim"/> con cómo se cerró: <c>committed-sent:wamid</c>,
/// <c>committed-failed:código</c> o <c>rolled-back</c>.</summary>
internal sealed class FakeOutboundMessages : IOutboundMessages
{
    /// <summary>Lo que el reclamo encuentra con <c>FOR UPDATE</c> (un request en vuelo con el mismo clientId).</summary>
    public ExistingOutbound? Existing { get; set; }

    /// <summary>Lo ya commiteado con ese clientId (§8.5 paso 2, sin candado).</summary>
    public ExistingOutbound? Committed { get; set; }

    public Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult(Committed);

    public List<FakeClaim> Claims { get; } = [];

    public Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken)
    {
        var claim = new FakeClaim(draft, Existing);
        Claims.Add(claim);
        return Task.FromResult<IOutboundClaim>(claim);
    }
}

internal sealed class FakeClaim(OutboundDraft draft, ExistingOutbound? existing) : IOutboundClaim
{
    public OutboundDraft Draft { get; } = draft;

    public string? Outcome { get; private set; }

    public bool Inserted => existing is null;

    public Guid MessageId => existing?.Id ?? Draft.MessageId;

    public ExistingOutbound? Existing => existing;

    // Como EF/Npgsql: un token ya cancelado hace fallar el commit o el rollback antes de llegar a la base.
    public Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        Close("committed-sent:" + wamid, cancellationToken);

    public Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        Close("committed-failed:" + failureCode.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) => Close("rolled-back", cancellationToken);

    public ValueTask DisposeAsync()
    {
        Outcome ??= "rolled-back";
        return ValueTask.CompletedTask;
    }

    private Task Close(string outcome, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Outcome is not null)
        {
            throw new InvalidOperationException($"The claim was already closed as {Outcome}.");
        }

        Outcome = outcome;
        return Task.CompletedTask;
    }
}

internal sealed class FakeConversationRepository : IConversationRepository
{
    public AutoAssignOutcome NextAutoAssign { get; set; } = AutoAssignOutcome.Assigned;

    public List<Guid> AutoAssigned { get; } = [];

    public Task<AutoAssignOutcome> TryAutoAssignAsync(Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AutoAssigned.Add(conversationId);
        return Task.FromResult(NextAutoAssign);
    }

    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> ExistsAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeMessageQueries : IMessageQueries
{
    public Dictionary<Guid, ReplyTargetRow> Targets { get; } = [];

    public Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ReplyTargetRow>>(Targets.Where(pair => ids.Contains(pair.Key)).ToDictionary());

    public Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeMemberNames(Guid memberId) : IMessagingMemberNames
{
    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            memberIds.Contains(memberId) ? new Dictionary<Guid, string> { [memberId] = "Andrés" } : new Dictionary<Guid, string>());
}
