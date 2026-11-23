using System.Globalization;
using System.Text.Json;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Lo que una petición necesita para evaluar plazos y fechar lo que escribe: el calendario y la zona del tenant, el instante
/// <paramref name="Now"/> y su «hoy» local, y el umbral de «por vencer». Se lee una vez por petición: el reloj del tenant no se vuelve a
/// pedir para fechar el cambio (revisión final, B5). <paramref name="ViewerMemberId"/> (fase 4a, plan G8) es la membresía de quien
/// pide, para el <c>IsMe</c> de cada miembro; null fuera de una sesión.</summary>
/// <param name="AutoCloseBusinessDays">Fase 4b (H22): los días hábiles del cierre automático del tenant.</param>
public sealed record SupportReadContext(
    SupportClockContext Clock,
    DateTimeOffset Now,
    DateOnly Today,
    int DueSoonBusinessDays,
    Guid? ViewerMemberId = null,
    int AutoCloseBusinessDays = SupportTenantSettings.DefaultAutoCloseBusinessDays);

/// <summary>
/// Arma las respuestas de ticket del contrato: el <see cref="SupportTicketDetailDto"/> de leer, crear y operar, y las filas de la
/// bandeja. Lo que la pantalla necesita saber (transiciones permitidas, cómo va el plazo, nombres de miembros y clientes) se
/// deriva acá y nunca se escribe: Backend for Frontend. Clientes y miembros se resuelven en lote.
/// </summary>
public sealed class SupportTicketDtoComposer(
    ISupportCustomerLookup customers,
    ISupportMemberDirectory members,
    ISupportTicketRepository tickets,
    ISupportBusinessCalendarSource calendars,
    ISupportSettingsRepository settings,
    ITenantClock tenantClock,
    ISupportReplyRepository replies,
    ISupportUnclassifiedRepository unclassified,
    ISupportTicketAccessRepository accesses,
    ISupportTicketAttachmentRepository attachments,
    ISupportLegalAcceptanceRepository legalAcceptances,
    ITenantModules tenantModules,
    ISupportViewer viewer,
    ISupportAutoCloseFeed autoCloseFeed)
{
    /// <summary>Lo que el contrato muestra de un miembro o cliente que ya no se resuelve (un miembro removido, un cliente borrado).</summary>
    internal const string UnknownName = "Sin nombre";

    /// <summary>Exige la fila de ajustes del tenant: quien llama ya pasó por <see cref="ISupportProvisioner.EnsureAsync"/>.</summary>
    public async Task<SupportReadContext> ReadContextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // El reloj primero: un tenant que no existe falla con tenancy.tenant.not_found antes de leer nada más.
        var tenantCalendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var clock = await calendars.GetAsync(tenantId, tenantCalendar.TimeZone, cancellationToken);
        var tenantSettings = await settings.GetAsync(tenantId, cancellationToken);
        var now = tenantCalendar.UtcNow;
        // Quién mira, una sola vez por petición (plan G8): null sin sesión, así el worker del hilo compone sin lanzar.
        var viewerMemberId = await viewer.MemberIdAsync(tenantId, cancellationToken);
        return new SupportReadContext(
            clock, now, clock.LocalDate(now), tenantSettings.DueSoonBusinessDays, viewerMemberId, tenantSettings.AutoCloseBusinessDays);
    }

    /// <summary>Las mismas lecturas que <see cref="ReadContextAsync"/>, sin exigir la fila de ajustes: <c>null</c> si el tenant nunca se
    /// aprovisionó (entonces tampoco tiene tickets). La usa la consulta pública, que no aprovisiona y lee el contexto también cuando el
    /// radicado no existe, para que el tiempo no lo delate (triage #4 del Bloque F). No lee quién mira: la consulta pública no tiene
    /// sesión ni miembros que marcar.</summary>
    public async Task<SupportReadContext?> TryReadContextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenantCalendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var clock = await calendars.GetAsync(tenantId, tenantCalendar.TimeZone, cancellationToken);
        if (await settings.FindAsync(tenantId, cancellationToken) is not { } tenantSettings)
        {
            return null;
        }

        var now = tenantCalendar.UtcNow;
        return new SupportReadContext(
            clock, now, clock.LocalDate(now), tenantSettings.DueSoonBusinessDays, AutoCloseBusinessDays: tenantSettings.AutoCloseBusinessDays);
    }

    public async Task<SupportTicketDetailDto> ComposeDetailAsync(SupportTicket ticket, CancellationToken cancellationToken) =>
        await ComposeDetailAsync(ticket, await ReadContextAsync(ticket.TenantId, cancellationToken), cancellationToken);

    /// <summary>Se llama después de guardar: la traza se lee de la base, no del agregado.</summary>
    public async Task<SupportTicketDetailDto> ComposeDetailAsync(
        SupportTicket ticket, SupportReadContext context, CancellationToken cancellationToken)
    {
        var events = await tickets.ListEventsAsync(ticket.TenantId, ticket.Id, cancellationToken);
        var details = events.Select(item => (Event: item, Details: EventDetails.Parse(item))).ToList();
        var ticketReplies = await replies.ListAsync(ticket.TenantId, ticket.Id, cancellationToken);
        // Fase 4b: los documentos de las respuestas formales, en una sola consulta.
        var documents = ticketReplies.Count == 0
            ? new Dictionary<Guid, SupportReplyDocument>()
            : (await replies.ListDocumentsAsync(ticket.TenantId, ticket.Id, cancellationToken)).ToDictionary(document => document.ReplyId);
        var unclassifiedCount = ticket.ConversationId is { } conversationId
            ? await unclassified.CountPendingAsync(ticket.TenantId, conversationId, cancellationToken)
            : 0;

        var memberIds = details
            .SelectMany(item => new[] { item.Event.ActorMemberId, item.Details.ToMember })
            .Append(ticket.AssigneeMemberId)
            .Append(ticket.CreatedByMemberId)
            .Concat(ticketReplies.Select(reply => (Guid?)reply.AuthorMemberId))
            .OfType<Guid>();
        var customerIds = details
            .Select(item => item.Details.ToCustomer)
            .Append(ticket.CustomerId)
            .OfType<Guid>();
        var membersById = await FindMembersAsync(ticket.TenantId, memberIds, cancellationToken);
        var customersById = await FindCustomersAsync(ticket.TenantId, customerIds, cancellationToken);
        var (dueState, daysLeft) = Evaluate(ticket, context);

        return new SupportTicketDetailDto(
            ticket.Id.Value,
            ticket.DisplayNumber,
            ticket.Subject,
            ticket.Description,
            ticket.Status.ToString(),
            ticket.Priority.ToString(),
            SupportTicketStatuses.AllowedFor(ticket.Status, ticket.IsInternal).Select(status => status.ToString()).ToList(),
            new SupportTicketTypeSnapshotDto(
                ticket.TypeId.Value, ticket.TypeName, ticket.TermBusinessDays, ticket.TermIsLegal, ticket.RequiresFormalResponse),
            new SupportClockDto(
                ticket.DueOn,
                dueState.ToString(),
                daysLeft,
                ticket.TermBusinessDays,
                ticket.TermIsLegal,
                ticket.PausedBusinessDays,
                ticket is { ExtendedAt: { } extendedAt, ExtensionReason: { } reason }
                    ? new SupportExtensionDto(ticket.ExtensionBusinessDays, reason, extendedAt)
                    : null,
                ticket.MaxExtensionBusinessDays(context.Today),
                await SupportAutoCloser.ForAsync(autoCloseFeed, ticket, context, cancellationToken)),
            ticket.Intake.ToString(),
            ticket.ManualMedium?.ToString(),
            // Una tarea no tiene canal de respuesta: sale null y no «None».
            ticket.ReplyChannel == SupportReplyChannel.None ? null : ticket.ReplyChannel.ToString(),
            ticket.CustomerId is { } customerId ? ToCustomerDto(customerId, customersById) : null,
            ticket.Requester is { } requester ? ToRequesterDto(requester) : null,
            ticket.AssigneeMemberId is { } assigneeId ? ToMemberDto(assigneeId, membersById, context.ViewerMemberId) : null,
            ticket.ResolutionCount,
            ticket.ReceivedAt,
            details.Select(item => ToEventDto(item.Event, item.Details, membersById, customersById, context.ViewerMemberId)).ToList(),
            ticket.Version,
            ticket.ConversationId,
            ticket.ConversationChannel,
            ticket.FollowThread,
            ticket.FollowOffAt,
            unclassifiedCount,
            ticketReplies
                .Select(reply => ToReplyDto(reply, membersById, context.ViewerMemberId, documents.GetValueOrDefault(reply.Id)))
                .ToList(),
            ticket.IsAnonymous,
            await CustomerCandidatesAsync(ticket, cancellationToken),
            await accesses.FindAsync(ticket.TenantId, ticket.Id, cancellationToken) is { } access
                ? new SupportConsultAccessDto(access.LastSentAt, access.LastSentChannel, access.LastFailureCode)
                : null,
            (await attachments.ListAsync(ticket.TenantId, ticket.Id, cancellationToken))
                .Select(item => new SupportAttachmentDto(item.Id, item.FileName, item.MimeType, item.SizeBytes))
                .ToList(),
            // Spec 2026-11-15 §7 (plan L19): a projection without the snapshot, already in the order of the enum.
            (await legalAcceptances.ListByTicketAsync(ticket.TenantId, ticket.Id, cancellationToken))
                .Select(item => new SupportLegalAcceptanceDto(item.Kind.ToString(), item.Version, item.AcceptedAt, item.VisitorIp))
                .ToList(),
            // El PIN y el enlace de consulta sólo los ponen el alta anónima, en su 201 (plan G3), y el PIN nuevo de la consola, en su
            // 200 (spec 2026-11-16, P-6); toda otra lectura los deja en null.
            IssuedPin: null,
            Surveys: (await tickets.ListSurveysAsync(ticket.TenantId, ticket.Id, cancellationToken)).Select(ToSurveyDto).ToList(),
            ConsultUrl: null,
            IsInternal: ticket.IsInternal,
            CreatedBy: ticket.CreatedByMemberId is { } creatorId ? ToMemberDto(creatorId, membersById, context.ViewerMemberId) : null);
    }

    // Plan F21: sin cliente y con documento en la foto, los clientes con ese documento (si customers está prendido): al radicar no se
    // vinculó porque había cero o varios. Con cliente, o sin documento, no hay candidatos; un anónimo nunca los tiene, porque no se le
    // puede vincular cliente (revisión final, I1).
    private async Task<IReadOnlyList<SupportCustomerCandidateDto>> CustomerCandidatesAsync(
        SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.IsAnonymous
            || ticket.CustomerId is not null
            || ticket.Requester is not { DocumentType: { } type, DocumentNumber: { } number }
            || await tenantModules.FindAsync(ticket.TenantId, cancellationToken) is not { } modules
            || !modules.IsEnabled(TenantModuleKeys.Customers))
        {
            return [];
        }

        var ids = await customers.FindIdsByDocumentAsync(ticket.TenantId, type, number, cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        var found = (await customers.FindManyAsync(ticket.TenantId, ids, cancellationToken)).ToDictionary(customer => customer.Id);
        return ids.Where(found.ContainsKey)
            .Select(id => new SupportCustomerCandidateDto(id, found[id].Name, found[id].IsActive))
            .ToList();
    }

    private static SupportSurveyDto ToSurveyDto(SupportTicketSurvey survey) => new(
        survey.ResolutionNumber,
        survey.Status.ToString(),
        survey.Channel?.ToString(),
        survey.ReasonCode,
        survey.SentAt,
        survey.Rating?.ToString(),
        survey.Comment,
        survey.AnsweredAt,
        survey.AnsweredVia?.ToString(),
        survey.PromptSentAt is not null);

    /// <summary>Una respuesta como la dibujan el detalle y el resultado de responder. Fase 4b: el firmante del documento sale con el nombre
    /// que imprimió el PDF, no con el que tenga hoy.</summary>
    internal static SupportReplyDto ToReplyDto(
        SupportTicketReply reply, IReadOnlyDictionary<Guid, string> membersById, Guid? viewerMemberId, SupportReplyDocument? document) => new(
        reply.Id,
        reply.ClientId,
        reply.Channel.ToString(),
        reply.Delivery.ToString(),
        reply.Text,
        reply.Status.ToString(),
        reply.FailureCode,
        reply.MessageId,
        ToMemberDto(reply.AuthorMemberId, membersById, viewerMemberId),
        reply.CreatedAt,
        reply.UploadId is not null,
        document is null
            ? null
            : new SupportReplyDocumentDto(
                document.FileName,
                document.SizeBytes,
                new SupportMemberDto(document.SignerMemberId, document.SignerName, document.SignerMemberId == viewerMemberId),
                document.SignerTitle));

    public async Task<IReadOnlyList<SupportTicketListItemDto>> ComposeListAsync(
        Guid tenantId, IReadOnlyList<SupportTicket> page, SupportReadContext context, CancellationToken cancellationToken)
    {
        var membersById = await FindMembersAsync(
            tenantId,
            page.SelectMany(ticket => new[] { ticket.AssigneeMemberId, ticket.CreatedByMemberId }).OfType<Guid>(),
            cancellationToken);
        var customersById = await FindCustomersAsync(
            tenantId, page.Select(ticket => ticket.CustomerId).OfType<Guid>(), cancellationToken);

        return page.Select(ticket =>
            {
                var (dueState, daysLeft) = Evaluate(ticket, context);
                return new SupportTicketListItemDto(
                    ticket.Id.Value,
                    ticket.DisplayNumber,
                    ticket.Subject,
                    ticket.Status.ToString(),
                    ticket.Priority.ToString(),
                    ticket.TypeName,
                    RequesterDisplayName(ticket, customersById),
                    ticket.AssigneeMemberId is { } assigneeId ? ToMemberDto(assigneeId, membersById, context.ViewerMemberId) : null,
                    ticket.ReceivedAt,
                    ticket.DueOn,
                    dueState.ToString(),
                    daysLeft,
                    ticket.IsInternal,
                    ticket.CreatedByMemberId is { } creatorId ? ToMemberDto(creatorId, membersById, context.ViewerMemberId) : null);
            })
            .ToList();
    }

    private static (SupportDueState State, int? DaysLeft) Evaluate(SupportTicket ticket, SupportReadContext context) =>
        SupportDueStates.Evaluate(ticket.Status, ticket.DueOn, context.Today, context.Clock.Calendar, context.DueSoonBusinessDays);

    // Cliente vinculado; si no, la foto del solicitante (nombres, razón social o «NIT número»). Un cliente vinculado que ya no se
    // encuentra cae a la foto, y sin foto a «Sin nombre».
    private static string RequesterDisplayName(SupportTicket ticket, IReadOnlyDictionary<Guid, SupportCustomerRef> customersById)
    {
        if (ticket.CustomerId is { } customerId && customersById.TryGetValue(customerId, out var customer))
        {
            return customer.Name;
        }

        var display = ticket.Requester?.DisplayName();
        return string.IsNullOrEmpty(display) ? UnknownName : display;
    }

    // Sólo nombres: el detalle y la bandeja no necesitan saber quién puede gestionar (revisión final, B4).
    private async Task<IReadOnlyDictionary<Guid, string>> FindMembersAsync(
        Guid tenantId, IEnumerable<Guid> memberIds, CancellationToken cancellationToken)
    {
        var ids = memberIds.Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, string>()
            : await members.NamesAsync(tenantId, ids, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, SupportCustomerRef>> FindCustomersAsync(
        Guid tenantId, IEnumerable<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, SupportCustomerRef>()
            : (await customers.FindManyAsync(tenantId, ids, cancellationToken)).ToDictionary(customer => customer.Id);
    }

    /// <summary>Un miembro del contrato con su <c>IsMe</c> (plan G8). Uno que ya no se resuelve sale «Sin nombre», y también puede ser
    /// quien mira.</summary>
    internal static SupportMemberDto ToMemberDto(Guid memberId, IReadOnlyDictionary<Guid, string> membersById, Guid? viewerMemberId) =>
        new(memberId, membersById.TryGetValue(memberId, out var name) ? name : UnknownName, memberId == viewerMemberId);

    private static SupportCustomerRefDto ToCustomerDto(Guid customerId, IReadOnlyDictionary<Guid, SupportCustomerRef> customersById) =>
        customersById.TryGetValue(customerId, out var customer)
            ? new SupportCustomerRefDto(customer.Id, customer.Name, customer.IsActive)
            : new SupportCustomerRefDto(customerId, UnknownName, false);

    private static SupportRequesterDto ToRequesterDto(TicketRequester requester) => new(
        requester.FirstNames,
        requester.LastNames,
        requester.BusinessName,
        requester.DocumentType,
        requester.DocumentNumber,
        requester.Email,
        requester.MobilePhone,
        requester.LandlinePhone,
        requester.CountryCode,
        requester.Address);

    private static SupportTicketEventDto ToEventDto(
        SupportTicketEvent item,
        EventDetails details,
        IReadOnlyDictionary<Guid, string> membersById,
        IReadOnlyDictionary<Guid, SupportCustomerRef> customersById,
        Guid? viewerMemberId) =>
        new(
            item.Ordinal,
            item.Kind.ToString(),
            item.OccurredAt,
            item.ActorMemberId is { } actorId ? ToMemberDto(actorId, membersById, viewerMemberId) : null,
            details.FromStatus,
            details.ToStatus,
            details.ToMember is { } memberId ? ToMemberDto(memberId, membersById, viewerMemberId) : null,
            details.FromTypeName,
            details.ToTypeName,
            details.ToCustomer is { } customerId ? ToCustomerDto(customerId, customersById) : null,
            details.MessageId,
            details.Follow,
            details.ReplyChannel,
            details.ExtensionBusinessDays,
            details.FromDueOn,
            details.ToDueOn,
            details.Reason,
            details.FromPriority,
            details.ToPriority,
            details.Formal);

    /// <summary>
    /// Los campos de <see cref="SupportTicketEvent.Details"/> según su <see cref="SupportTicketEvent.Kind"/>, con la forma que escribe
    /// <c>SupportTicket</c>: <c>Created</c> <c>{"to": estado}</c>; <c>StatusChanged</c> <c>{"from","to"}</c> estados;
    /// <c>AssigneeChanged</c> y <c>CustomerChanged</c> <c>{"from","to"}</c> ids o null; <c>TypeChanged</c>
    /// <c>{"from":{"id","name"},"to":{"id","name"}}</c>. Fase 2: los cuatro de mensajes <c>{"messageId"}</c>;
    /// <c>FollowThreadChanged</c> <c>{"follow","at"}</c>; <c>ReplySent</c> <c>{"replyId","channel","delivery","messageId"}</c>;
    /// <c>ConversationLinked</c> <c>{"conversationId","channel"}</c>, que el detalle ya trae arriba. Fase 4a: <c>Extended</c>
    /// <c>{"businessDays","reason","fromDueOn","toDueOn"}</c>, con las fechas en <c>yyyy-MM-dd</c>. <c>PriorityChanged</c>
    /// <c>{"from","to"}</c> prioridades por nombre.
    /// </summary>
    private sealed record EventDetails(
        string? FromStatus,
        string? ToStatus,
        Guid? ToMember,
        string? FromTypeName,
        string? ToTypeName,
        Guid? ToCustomer,
        Guid? MessageId = null,
        bool? Follow = null,
        string? ReplyChannel = null,
        int? ExtensionBusinessDays = null,
        DateOnly? FromDueOn = null,
        DateOnly? ToDueOn = null,
        string? Reason = null,
        string? FromPriority = null,
        string? ToPriority = null,
        bool? Formal = null)
    {
        private static readonly EventDetails Empty = new(null, null, null, null, null, null);

        public static EventDetails Parse(SupportTicketEvent item)
        {
            using var document = JsonDocument.Parse(item.Details);
            var root = document.RootElement;
            return item.Kind switch
            {
                SupportTicketEventKind.Created => Empty with { ToStatus = Text(root, "to") },
                SupportTicketEventKind.StatusChanged => Empty with { FromStatus = Text(root, "from"), ToStatus = Text(root, "to") },
                SupportTicketEventKind.AssigneeChanged => Empty with { ToMember = Id(root, "to") },
                SupportTicketEventKind.CustomerChanged => Empty with { ToCustomer = Id(root, "to") },
                SupportTicketEventKind.TypeChanged => Empty with
                {
                    FromTypeName = Text(Child(root, "from"), "name"),
                    ToTypeName = Text(Child(root, "to"), "name"),
                },
                SupportTicketEventKind.MessagePinned or SupportTicketEventKind.MessageReleased or SupportTicketEventKind.MessageClassified
                    or SupportTicketEventKind.MessageDismissed => Empty with { MessageId = Id(root, "messageId") },
                SupportTicketEventKind.FollowThreadChanged => Empty with
                {
                    Follow = Child(root, "follow") is { ValueKind: JsonValueKind.True or JsonValueKind.False } follow
                        ? follow.GetBoolean()
                        : null,
                },
                SupportTicketEventKind.ReplySent => Empty with
                {
                    MessageId = Id(root, "messageId"),
                    ReplyChannel = Text(root, "channel"),
                    // Fase 4b: una respuesta anterior no lo escribía; la traza la trata como no formal.
                    Formal = Child(root, "formal").ValueKind == JsonValueKind.True,
                },
                SupportTicketEventKind.Extended => Empty with
                {
                    ExtensionBusinessDays = Child(root, "businessDays") is { ValueKind: JsonValueKind.Number } days
                        && days.TryGetInt32(out var value)
                            ? value
                            : null,
                    FromDueOn = Date(root, "fromDueOn"),
                    ToDueOn = Date(root, "toDueOn"),
                    Reason = Text(root, "reason"),
                },
                SupportTicketEventKind.DueDateChanged => Empty with
                {
                    FromDueOn = Date(root, "fromDueOn"),
                    ToDueOn = Date(root, "toDueOn"),
                },
                SupportTicketEventKind.PriorityChanged => Empty with
                {
                    FromPriority = Text(root, "from"),
                    ToPriority = Text(root, "to"),
                },
                _ => Empty,
            };
        }

        private static JsonElement Child(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

        private static string? Text(JsonElement element, string name) =>
            Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        private static DateOnly? Date(JsonElement element, string name) =>
            Text(element, name) is { } text
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;

        private static Guid? Id(JsonElement element, string name) =>
            Child(element, name) is { ValueKind: JsonValueKind.String } value && value.TryGetGuid(out var id) ? id : null;
    }
}
