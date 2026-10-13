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

    public InMemoryMediaStore Store { get; } = new();

    public FakeUploadStaging Staging { get; } = new();

    public CreateMediaUploadSessionHandler CreateMediaUploadSessionHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        return new CreateMediaUploadSessionHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Connections,
            new FakeMemberNames(MemberId),
            membership,
            TenantModules,
            new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(Now),
            Staging,
            new CreateMediaUploadSessionValidator());
    }

    public FakeMessageTemplates Templates { get; } = new();

    public FakeCustomerDirectory Customers { get; } = new();

    public FakeConversationOpener Opener => _opener ??= new FakeConversationOpener(this);

    private FakeConversationOpener? _opener;

    public ConversationRow OpenConversation(
        int? lastInboundHoursAgo, bool resolved = false, Guid? assignedTo = null, Guid? customerId = null, string? userId = null, string? waId = "573001234567")
    {
        var row = new ConversationRow(
            Guid.CreateVersion7(), TenantId, ConnectionId, UserId: userId, WaId: waId, Username: null, "Laura",
            CustomerId: customerId, AssignedMemberId: assignedTo, resolved ? ConversationStatus.Resolved : ConversationStatus.Open, 0,
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

    public SendTemplateMessageHandler SendTemplateHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        return new SendTemplateMessageHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Meta,
            Connections,
            new FakeMemberNames(MemberId),
            membership,
            Templates,
            Customers,
            TenantModules,
            new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(Now),
            new SendTemplateMessageValidator());
    }

    public StartConversationHandler StartHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        var memberNames = new FakeMemberNames(MemberId);
        var context = new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage);
        return new StartConversationHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Meta,
            Connections,
            memberNames,
            membership,
            Templates,
            Customers,
            Opener,
            new ConversationSummaryBuilder(Connections, Customers, memberNames, new CallerMembership(membership, context)),
            TenantModules,
            context,
            new FakeClock(Now),
            new StartConversationValidator());
    }

    public ListTemplatesHandler ListTemplatesHandler(params string[] permissions) =>
        new(Templates, TenantModules, new FakeExecutionContext(TenantId, SubjectId, permissions));

    public ListMessagingConnectionsHandler ListConnectionsHandler(params string[] permissions) =>
        new(Connections, TenantModules, new FakeExecutionContext(TenantId, SubjectId, permissions));

    public SendAudioMessageHandler SendAudioHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        return new SendAudioMessageHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Meta,
            Store,
            Connections,
            new FakeMemberNames(MemberId),
            membership,
            TenantModules,
            new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(Now),
            new SendAudioMessageValidator());
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

    /// <summary>Spec 2026-10-11 §6.2: los nombres de las conexiones (Active o no); sin entrada, «Conexión eliminada».</summary>
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>(Names));

    /// <summary>Spec 2026-10-11 §5.1: <c>null</c> = conexión que no está Active (o de otro tenant).</summary>
    public MessagingTemplateSource? TemplateSource { get; set; } = new("222", "token");

    public List<MessagingConnectionDto> Active { get; } = [];

    public Task<MessagingTemplateSource?> ResolveTemplateSourceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(TemplateSource);

    public Task<IReadOnlyList<MessagingConnectionDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessagingConnectionDto>>(Active.ToList());

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

internal sealed record UploadedMedia(MessagingSender Sender, byte[] Content, string MimeType);

internal sealed record SentTemplate(MessagingSender Sender, SendTarget Target, TemplateSend Template, string CallbackData);

internal sealed record SentAudio(MessagingSender Sender, SendTarget Target, string MediaId, string CallbackData, string? ContextWamid);

internal sealed class FakeWhatsAppClient : IWhatsAppCloudClient
{
    public SendResult NextSend { get; set; } = new(SendOutcome.Sent, "wamid.default", null, null);

    public List<SentText> Sends { get; } = [];

    /// <summary>Corre mientras "Meta" procesa: p. ej., la persona cierra la pestaña (se cancela el request).</summary>
    public Action? DuringSend { get; set; }

    /// <summary>Como HttpClient: Meta ya recibió el mensaje, pero un token cancelado a mitad de la llamada lanza.</summary>
    public Task<SendResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        Sends.Add(new SentText(sender, target, body, callbackData, contextWamid));
        DuringSend?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextSend);
    }

    /// <summary>Spec 2026-10-11 §6.3 paso 2: lo que responde la subida a Meta.</summary>
    public MessagingGraphResult<string> NextUpload { get; set; } = new("media.default", null);

    public List<UploadedMedia> Uploads { get; } = [];

    /// <summary>Lo inesperado durante la subida a Meta (una cancelación, un bug): ya se subió a R2.</summary>
    public Exception? UploadException { get; set; }

    public List<SentAudio> AudioSends { get; } = [];

    public Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken)
    {
        Uploads.Add(new UploadedMedia(sender, content.ToArray(), mimeType));
        if (UploadException is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(NextUpload);
    }

    /// <summary>Como <see cref="SendTextAsync"/>: devuelve <see cref="NextSend"/> y corre <see cref="DuringSend"/>.</summary>
    public Task<SendResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        AudioSends.Add(new SentAudio(sender, target, mediaId, callbackData, contextWamid));
        DuringSend?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextSend);
    }

    /// <summary>Spec 2026-10-11 §5.1: lo que responde la lista de plantillas, y cuántas veces se pidió.</summary>
    public MessagingGraphResult<IReadOnlyList<System.Text.Json.JsonElement>> NextTemplates { get; set; } = new([], null);

    private int _templateListCalls;

    public int TemplateListCalls => _templateListCalls;

    /// <summary>Retiene la respuesta de la lista hasta que la prueba lo suelte: sirve para ver cuántas llamadas se juntan en vuelo.</summary>
    public Func<Task>? BeforeTemplateList { get; set; }

    public async Task<MessagingGraphResult<IReadOnlyList<System.Text.Json.JsonElement>>> ListTemplatesAsync(MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _templateListCalls);
        if (BeforeTemplateList is { } gate)
        {
            await gate();
        }

        return NextTemplates;
    }

    public List<SentTemplate> TemplateSends { get; } = [];

    /// <summary>Como <see cref="SendTextAsync"/>: devuelve <see cref="NextSend"/> y corre <see cref="DuringSend"/>.</summary>
    public Task<SendResult> SendTemplateAsync(
        MessagingSender sender, SendTarget target, TemplateSend send, string callbackData, CancellationToken cancellationToken)
    {
        TemplateSends.Add(new SentTemplate(sender, target, send, callbackData));
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

    /// <summary>Lo que recibió el commit como id del medio en Meta (spec 2026-10-11 §6.5).</summary>
    public string? MetaMediaId { get; private set; }

    // Como EF/Npgsql: un token ya cancelado hace fallar el commit o el rollback antes de llegar a la base.
    public Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
    {
        MetaMediaId = metaMediaId;
        return Close("committed-sent:" + wamid, cancellationToken);
    }

    public Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
    {
        MetaMediaId = metaMediaId;
        return Close("committed-failed:" + failureCode.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

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

    public Task<string?> FindDisplayNameAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(id == memberId ? "Andrés" : null);
}

/// <summary>Spec 2026-10-11 §6.3: R2 en un diccionario. <see cref="UploadFailure"/> simula que R2 no aceptó el audio.</summary>
internal sealed class InMemoryMediaStore : IMessagingMediaStore
{
    public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

    public List<string> Deleted { get; } = [];

    public Exception? UploadFailure { get; set; }

    public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        if (UploadFailure is { } failure)
        {
            throw failure;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Objects[key] = (buffer.ToArray(), contentType);
    }

    public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Exception? DeleteFailure { get; set; }

    /// <summary>Los tokens con que se pidió borrar: el borrado best effort va con <c>CancellationToken.None</c>.</summary>
    public List<CancellationToken> DeleteTokens { get; } = [];

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Deleted.Add(key);
        DeleteTokens.Add(cancellationToken);
        if (DeleteFailure is { } failure)
        {
            throw failure;
        }

        Objects.Remove(key);
        return Task.CompletedTask;
    }
}
