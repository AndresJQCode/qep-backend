using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-21 §5.3, D-M5 y D-M14: lo que el motor le hace a la conversación de Mensajería, por el agregado <see cref="Conversation"/>
/// y con sus reglas. Los eventos del hilo se escriben como en <c>ConversationAssignmentSteps.CommitAsync</c> (ConversationAssignment.cs),
/// pero la auditoría es de sistema (<see cref="IMessagingAuditRecorder.RecordBySystem"/>) y el evento no lleva actor: el bot no es una
/// persona. Cada cambio se guarda y se avisa antes de volver; una regla del agregado que no se cumple sale como el
/// <see cref="MessagingDomainException"/> de siempre y una versión vieja como <c>concurrency.conflict</c> (<c>MessagingSaveChanges</c>).
/// <para>
/// Los cambios cargan el agregado en un scope propio, como <see cref="ShopifyOrderWriter"/>: en el scope del procesador el
/// <c>MessagingDbContext</c> ya rastrea la conversación que cargó un cambio anterior, y entre los dos el envío del bot sube la versión
/// por SQL (<c>MarkRepliedAsync</c>) y la ingesta también. Releerla de un tracker viejo devolvería la versión de antes y el segundo
/// cambio de la misma unidad (arrancar y después traspasar) sería un 412 contra sí mismo.
/// </para>
/// <para>
/// Ruling P2-R22: el aviso en vivo va después del commit de Mensajería y es best effort, como en <c>OutboundSettlement</c>: si falla, el
/// cambio ya quedó y la unidad del motor no puede deshacerse por eso (reintentaría un traspaso que ya ocurrió). El polling de la bandeja
/// lo cubre.
/// </para>
/// </summary>
internal sealed partial class AutomationConversations(
    IServiceScopeFactory scopes,
    IConversationQueries queries,
    IConversationRepository repository,
    IMessagingNotifier notifier,
    ILogger<AutomationConversations> logger) : IAutomationConversations
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Live notification for conversation {ConversationId} of tenant {TenantId} failed after the automation change was committed.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handoff of conversation {ConversationId} of tenant {TenantId} left unassigned: member {MemberId} can no longer reply.")]
    private static partial void LogAssigneeCannotReply(ILogger logger, Guid tenantId, Guid conversationId, Guid memberId);

    public async Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        // Sin seguimiento (ConversationQueries): lo que lee el motor no queda en ningún tracker.
        await queries.FindAsync(tenantId, conversationId, cancellationToken) is { } row
            ? new AutomationConversationState(
                row.Status.ToString(), row.AssignedMemberId, row.AutomationStatus.ToString(), row.AutomationChatbotId, row.ConnectionId,
                row.LastInboundAt, row.ProfileName, row.WaId, row.AutomationResumedAt, row.AutomationPauseReason?.ToString(),
                row.Channel.ToString(), row.BlockedAt is not null, row.LastInboundAt?.Add(Conversation.WindowLength), row.AutomationPausedAt)
            : null;

    public Task<bool> WasResolvedSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken) =>
        queries.HasResolvedEventSinceAsync(tenantId, conversationId, since, cancellationToken);

    public Task<bool> WasTakenSinceAsync(Guid tenantId, Guid conversationId, DateTimeOffset since, CancellationToken cancellationToken) =>
        queries.HasAdvisorEventSinceAsync(tenantId, conversationId, since, cancellationToken);

    public async Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        conversation.StartAutomation(chatbotId, services.Clock.UtcNow);
        conversation.SetAutomationStep(firstStepTitle);
        // Sin evento en el hilo ni auditoría de Mensajería (§5.2 paso 3 sólo pide guardar y notificar): el arranque lo audita la corrida
        // en Automations (automations.run.started).
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken)
    {
        // UPDATE directo y no el agregado: SetAutomationStep no hace Touch, pero guardar el agregado igual compara la versión, y cada
        // entrante que llega mientras el bot avanza la sube (InboundIngestion.cs).
        if (await repository.SetAutomationStepAsync(tenantId, conversationId, stepTitle, cancellationToken))
        {
            await NotifyAsync(tenantId, conversationId, () => notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
        }
    }

    public async Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken)
    {
        // UPDATE directo por la misma razón que SetStepAsync: el agregado compararía una versión que la ingesta sube por SQL.
        if (await repository.SetAutomationChatbotAsync(tenantId, conversationId, chatbotId, cancellationToken))
        {
            await NotifyAsync(tenantId, conversationId, () => notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
        }
    }

    public async Task HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        var now = services.Clock.UtcNow;
        // Spec 2026-10-25 D-5: el miembro pudo perder el permiso o la membresía desde que se eligió; en ese caso cae sin asignar, como
        // hoy, en vez de dejar la conversación con alguien que no puede responderla.
        Guid? assignee = assignToMemberId is { } id
            && await scope.ServiceProvider.GetRequiredService<IMessagingAssignees>().CanReplyAsync(tenantId, id, cancellationToken)
                ? id
                : null;

        // Spec 2026-11-17 adenda D-6: la foto de los nombres del chatbot y del agente del bloque, del mismo diccionario que la delegación.
        var names = cause is { ChatbotId: { } chatbotId, AgentId: { } agentId }
            ? await services.Names.ResolveAsync(tenantId, [chatbotId, agentId], cancellationToken)
            : null;

        // Spec 2026-10-24 §5.3: la causa viaja en el mismo evento, un solo cambio con el mismo now; el dominio de Mensajería la limpia.
        // Spec 2026-10-25 D-1, D-7: pausa y asignación en un solo cambio, sin evento Transferred.
        var before = Before.Of(conversation);
        var change = conversation.HandOffAutomation(ToHandoff(cause, assignee, names), now);
        await services.ReleaseDelegationAsync(conversation, before, now, cancellationToken);
        services.Events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
        services.Audit.RecordBySystem(tenantId, MessagingAuditActions.AutomationPaused, conversation.Id, now);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        // Sólo se avisa una vez confirmado el traspaso: un fallo al guardar no deja un log de algo que no ocurrió.
        if (assignToMemberId is not null && assignee is null)
        {
            LogAssigneeCannotReply(logger, tenantId, conversationId, assignToMemberId.Value);
        }

        await NotifyAsync(tenantId, conversationId, async () =>
        {
            await services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken);
            // D-M5: sin asignar vuelve a la bandeja y suena a todo el tenant, como un entrante sin asignar; asignada (D-1), sólo a ese miembro.
            if (await services.Inbox.FindLastInboundIdAsync(tenantId, conversationId, cancellationToken) is { } lastInbound)
            {
                await services.Notifier.InboundMessageAsync(tenantId, conversationId, lastInbound, assignedMemberId: assignee, cancellationToken);
            }
        });
    }

    /// <summary>Spec 2026-10-24 §5.3: el kind se traduce por nombre (<c>HandoffCauseKindParityTests</c> amarra los dos enums).</summary>
    private static AutomationHandoff ToHandoff(HandoffCause cause, Guid? assignedMemberId, IReadOnlyDictionary<Guid, string>? names)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return AutomationHandoff.Create(
            Enum.Parse<AutomationHandoffKind>(cause.Kind.ToString()),
            cause.Code,
            cause.NodeId,
            cause.NodeTitle,
            cause.ConnectionName,
            cause.ConnectionReason,
            cause.Http is { } http
                ? new AutomationHandoffHttp(http.NodeId, http.NodeTitle, http.Outcome, http.ErrorCode, http.Status, http.ConnectionName)
                : null,
            assignedMemberId,
            chatbotName: cause.ChatbotId is { } chatbotId ? names?.GetValueOrDefault(chatbotId) : null,
            agentName: cause.AgentId is { } agentId ? names?.GetValueOrDefault(agentId) : null);
    }

    public async Task ResolveAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid? reasonId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        // El motivo del bloque se eligió al editar el flujo (o vino en un chatbot importado de otra empresa): si ya no está activo en este
        // tenant se resuelve sin motivo, en vez de caer por la FK o enlazar un motivo ajeno.
        var reason = reasonId is { } id
            ? await scope.ServiceProvider.GetRequiredService<IResolutionReasonRepository>().FindActiveAsync(tenantId, id, cancellationToken)
            : null;
        var now = services.Clock.UtcNow;
        var before = Before.Of(conversation);
        conversation.Resolve(now, reason?.Id);
        await services.ReleaseDelegationAsync(conversation, before, now, cancellationToken);
        // Spec 2026-10-10 §6.1.5: el evento lleva quién. El bot no es una membresía, así que Actor queda null (ConversationEventJson lo
        // omite) y el chatbot va en ChatbotId: el hilo lo resuelve a nombre al leer, como SentByChatbotId (spec 2026-10-21 D-I3).
        services.Events.Record(
            tenantId, conversation.ConnectionId, conversation.Id,
            new ConversationEvent(ConversationEventType.Resolved, Actor: null, ReasonId: reason?.Id, ChatbotId: chatbotId), now);
        services.Audit.RecordBySystem(tenantId, MessagingAuditActions.Resolved, conversation.Id, now);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        // D-M14 devuelve a None sólo lo que el bot atiende: una pausada la decide una persona (EndAutomation lanzaría) y una resuelta ya
        // quedó en None. Un segundo End, o el barrido de abandonadas sobre una que un asesor ya pausó, no hace nada.
        if (conversation.Status != ConversationStatus.Open || conversation.AutomationStatus != ConversationAutomationStatus.Active)
        {
            return;
        }

        var now = services.Clock.UtcNow;
        var before = Before.Of(conversation);
        conversation.EndAutomation(now);
        await services.ReleaseDelegationAsync(conversation, before, now, cancellationToken);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task<Guid> DelegateToAgentAsync(
        Guid tenantId, Guid conversationId, DelegationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        var now = services.Clock.UtcNow;
        var delegationId = Guid.CreateVersion7();
        // Adenda D-5: la foto de los nombres va en el evento (los dos ids salen del mismo diccionario de bots).
        var names = await services.Names.ResolveAsync(tenantId, [request.ChatbotId, request.AgentId], cancellationToken);
        // Spec 2026-11-17 §6.1: la regla del agregado va primero; si una persona tomó la conversación lanza y nada se agrega.
        var change = conversation.DelegateToAgent(
            delegationId, request.AgentId, now, names.GetValueOrDefault(request.ChatbotId), names.GetValueOrDefault(request.AgentId));
        // Revisión de la rama, hallazgo 1: una transacción explícita porque mover las filas es SQL que corre en el acto. La versión de la
        // conversación ya está cargada: un entrante que la ingesta encole después la sube y el guardado choca (se reintenta la unidad).
        await using var transaction = await services.UnitOfWork.BeginTransactionAsync(cancellationToken);
        // Hallazgo 3: el agregado ya exigió que la conversación no apunte a ninguna (es del chatbot), así que toda delegación viva de ella está
        // huérfana y frenaría la nueva por IX_bot_delegations_live. Se cierra antes de agregar; su corrida ya no está viva (ésta lo está).
        await services.Delegations.InterruptLiveAsync(tenantId, conversationId, now, cancellationToken);
        // Los entrantes que la ingesta encoló para el chatbot mientras su unidad corría pasan al agente. Son más viejos que la fila Delegated,
        // así que el agente los atiende primero y abre la sesión del bloque con ellos (AiInboxProcessor.TryOpenAsync).
        await services.Inbox.ReassignPendingEngineAsync(tenantId, conversationId, BotKind.Agent, request.InboxRowId, cancellationToken);
        services.Delegations.Add(BotDelegation.Start(
            delegationId, tenantId, conversationId, conversation.ConnectionId, request.ChatbotId, request.RunId, request.NodeId, request.AgentId,
            request.LastInboundMessageId, request.BriefJson, now));
        services.Inbox.EnqueueDelegated(tenantId, conversation.ConnectionId, conversationId, delegationId, now);
        services.Events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
        services.Audit.RecordBySystem(tenantId, MessagingAuditActions.AutomationDelegated, conversation.Id, now);
        // Delegación, conversación, filas de la cola, evento y auditoría: una sola transacción (D-B3).
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
        return delegationId;
    }

    public async Task<DelegationResult?> ReadDelegationResultAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken)
    {
        // Scope propio: lo que lee el motor no queda en ningún tracker (como GetAsync).
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IBotDelegationRepository>().FindAsync(tenantId, delegationId, cancellationToken)
            is { } delegation
            ? new DelegationResult(
                delegation.Id, delegation.ConversationId, delegation.ChatbotId, delegation.RunId, delegation.NodeId, delegation.AgentId,
                delegation.Status.ToString(), delegation.Result)
            : null;
    }

    /// <summary>P2-R22: lo que falle después del commit se registra y no sube. La cancelación sí sube: es el worker que se apaga.</summary>
    private async Task NotifyAsync(Guid tenantId, Guid conversationId, Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, exception, tenantId, conversationId);
        }
    }

    /// <summary>Lo de Mensajería que un cambio usa, resuelto en su scope propio.</summary>
    private sealed class Services(IServiceProvider provider)
    {
        public IConversationEvents Events { get; } = provider.GetRequiredService<IConversationEvents>();

        public IMessagingAuditRecorder Audit { get; } = provider.GetRequiredService<IMessagingAuditRecorder>();

        public IMessagingUnitOfWork UnitOfWork { get; } = provider.GetRequiredService<IMessagingUnitOfWork>();

        public IMessagingNotifier Notifier { get; } = provider.GetRequiredService<IMessagingNotifier>();

        public IAutomationInbox Inbox { get; } = provider.GetRequiredService<IAutomationInbox>();

        public IClock Clock { get; } = provider.GetRequiredService<IClock>();

        public IBotDelegationRepository Delegations => provider.GetRequiredService<IBotDelegationRepository>();

        public IAutomationsChatbotNames Names => provider.GetRequiredService<IAutomationsChatbotNames>();

        public async Task<Conversation> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            await provider.GetRequiredService<IConversationRepository>().FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw new ResourceNotFoundException(MessagingErrorCodes.ConversationNotFound, $"Conversation '{conversationId}' was not found.");

        /// <summary>
        /// Revisión de la rama, hallazgo 1: el chatbot traspasa, resuelve o termina una conversación que tiene delegada (un fallo del flujo
        /// con la corrida esperando al agente, o una carrera con la delegación). El agregado limpia la columna; sin esto la delegación quedaba
        /// <c>Live</c> y la sesión del agente seguía esperando. Es la regla de los handlers de Mensajería (<see cref="ReleaseRunSignal"/>):
        /// delegación <c>Interrupted</c> y una señal a cada bot, en el mismo guardado. Sin delegación no hace nada: el bot no se suelta a sí
        /// mismo (D-6 de 2026-11-03).
        /// </summary>
        public Task ReleaseDelegationAsync(Conversation conversation, Before before, DateTimeOffset now, CancellationToken cancellationToken) =>
            before.Delegation is null
                ? Task.CompletedTask
                : Inbox.EnqueueIfAssistedAsync(Delegations, conversation, before.Status, before.Bot, before.Delegation, changes: 1, now, cancellationToken);
    }

    /// <summary>El asistente antes del cambio: el agregado limpia el bot y la delegación al sacarla de <c>Active</c>.</summary>
    private sealed record Before(ConversationAutomationStatus Status, BotKind? Bot, Guid? Delegation)
    {
        public static Before Of(Conversation conversation) =>
            new(conversation.AutomationStatus, conversation.AutomationBotKind, conversation.AutomationDelegationId);
    }
}
