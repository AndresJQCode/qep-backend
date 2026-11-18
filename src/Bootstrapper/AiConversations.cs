using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Ai.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 §6.2–§6.6: lo que un agente de IA le hace a la conversación de Mensajería, por el agregado <see cref="Conversation"/> y con
/// sus reglas, igual que <see cref="AutomationConversations"/> para los chatbots: cada cambio en un scope propio (la versión la sube la ingesta
/// por SQL), auditoría de sistema, el evento del hilo sin actor y el aviso en vivo después del commit y best effort (P2-R22).
/// <para>
/// <b>Acoplamiento por nombre</b> en <see cref="ReadMessagesAsync"/>: lee <c>messaging.messages</c> (<c>id</c>, <c>tenant_id</c>, <c>kind</c>,
/// <c>text</c>, <c>caption</c>) con SQL. Renombrar una de esas columnas compila y deja al agente sin historial.
/// </para>
/// </summary>
internal sealed partial class AiConversations(
    IServiceScopeFactory scopes,
    IConversationQueries queries,
    MessagingDbContext messaging,
    IClock clock,
    ILogger<AiConversations> logger) : IAiConversations
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Live notification for conversation {ConversationId} of tenant {TenantId} failed after the agent change was committed.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent handoff of conversation {ConversationId} of tenant {TenantId} left unassigned: member {MemberId} can no longer reply.")]
    private static partial void LogAssigneeCannotReply(ILogger logger, Guid tenantId, Guid conversationId, Guid memberId);

    public async Task<AiConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        if (await queries.FindAsync(tenantId, conversationId, cancellationToken) is not { } row)
        {
            return null;
        }

        var rule = ChannelOutboundRules.For(row.Channel);
        var now = clock.UtcNow;
        var expiresAt = rule.Window is { } window && row.LastInboundAt is { } last ? last.Add(window) : (DateTimeOffset?)null;
        return new AiConversationState(
            row.Status.ToString(),
            row.AssignedMemberId,
            row.AutomationStatus.ToString(),
            row.AutomationBotKind?.ToString(),
            row.AutomationAgentId,
            row.ConnectionId,
            row.Channel.ToString(),
            row.CustomerId,
            row.AutomationResumedAt,
            row.BlockedAt is not null,
            ChannelOutboundRules.IsWindowOpen(row.Channel, row.LastInboundAt, now),
            expiresAt is { } expires ? (expires > now ? expires - now : TimeSpan.Zero) : null,
            rule.TextMaxLength,
            row.AutomationDelegationId);
    }

    public async Task StartAsync(Guid tenantId, Guid conversationId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        conversation.StartAutomation(BotRef.Agent(agentId), services.Clock.UtcNow);
        // Como los chatbots: sin evento ni auditoría de Mensajería; el arranque es la sesión de Ai.
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task HandoffAsync(
        Guid tenantId, Guid conversationId, AgentHandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cause);
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        var now = services.Clock.UtcNow;
        Guid? assignee = assignToMemberId is { } id
            && await scope.ServiceProvider.GetRequiredService<IMessagingAssignees>().CanReplyAsync(tenantId, id, cancellationToken)
                ? id
                : null;

        // §4.4: el kind se traduce por nombre (HandoffCauseKindParityTests amarra los enums); Mensajería limpia código y proveedor.
        var handoff = AutomationHandoff.Create(
            Enum.Parse<AutomationHandoffKind>(cause.Kind.ToString()), cause.Code, null, null, cause.ConnectionName, cause.ConnectionReason, null,
            assignee, cause.Provider);
        var change = conversation.HandOffAutomation(handoff, now);
        services.Events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
        services.Audit.RecordBySystem(tenantId, MessagingAuditActions.AutomationPaused, conversation.Id, now);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (assignToMemberId is not null && assignee is null)
        {
            LogAssigneeCannotReply(logger, tenantId, conversationId, assignToMemberId.Value);
        }

        await NotifyAsync(tenantId, conversationId, async () =>
        {
            await services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken);
            // D-M5: sin asignar suena a todo el tenant; asignada, sólo a ese miembro.
            if (await services.Inbox.FindLastInboundIdAsync(tenantId, conversationId, cancellationToken) is { } lastInbound)
            {
                await services.Notifier.InboundMessageAsync(tenantId, conversationId, lastInbound, assignedMemberId: assignee, cancellationToken);
            }
        });
    }

    public async Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        if (conversation.Status != ConversationStatus.Open
            || conversation.AutomationStatus != ConversationAutomationStatus.Active
            || conversation.AutomationBotKind != BotKind.Agent)
        {
            return;
        }

        conversation.EndAutomation(services.Clock.UtcNow);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, AiThreadMessage>> ReadMessagesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return new Dictionary<Guid, AiThreadMessage>();
        }

        var ids = messageIds.Distinct().ToArray();
        var rows = await messaging.Database.SqlQuery<MessageTextRow>(
            $"""
            SELECT id AS "Id", kind AS "Kind", COALESCE(text, caption) AS "Text"
              FROM messaging.messages
             WHERE tenant_id = {tenantId} AND id = ANY({ids})
            """).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => new AiThreadMessage(row.Id, KindOf((MessageKind)row.Kind), row.Text));
    }

    public async Task<AiDelegation?> ReadDelegationAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken)
    {
        // Scope propio: la lectura no deja la delegación en el tracker del procesador.
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IBotDelegationRepository>().FindAsync(tenantId, delegationId, cancellationToken)
            is { } delegation
            ? new AiDelegation(
                delegation.Id, delegation.ConversationId, delegation.ConnectionId, delegation.ChatbotId, delegation.RunId, delegation.NodeId,
                delegation.AgentId, delegation.LastInboundMessageId, delegation.Brief, delegation.Status.ToString(), delegation.StartedAt)
            : null;
    }

    public async Task ReturnToChatbotAsync(
        Guid tenantId, Guid conversationId, Guid delegationId, string resultJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        var output = OutputOf(resultJson);
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var conversation = await services.LoadAsync(tenantId, conversationId, cancellationToken);
        var delegation = await services.Delegations.FindAsync(tenantId, delegationId, cancellationToken);
        if (delegation is null || delegation.ConversationId != conversationId)
        {
            throw new MessagingDomainException(MessagingErrorCodes.DelegationNotLive, "That delegation is not one of the conversation.");
        }

        var now = services.Clock.UtcNow;
        var names = await services.Names.ResolveAsync(tenantId, [delegation.ChatbotId, delegation.AgentId], cancellationToken);
        // Las dos reglas antes de agregar nada: la delegación sigue viva y sigue siendo la de la conversación (una toma la interrumpe).
        delegation.Return(resultJson, now);
        var change = conversation.ReturnFromAgent(
            delegationId, delegation.ChatbotId, output, now, names.GetValueOrDefault(delegation.ChatbotId), names.GetValueOrDefault(delegation.AgentId));
        // Revisión de la rama, hallazgo 6 (el espejo del 1): una transacción explícita porque mover las filas es SQL que corre en el acto; con
        // la versión de la conversación ya cargada, un entrante que entre en el medio hace chocar el guardado y el retorno se reintenta.
        await using var transaction = await services.UnitOfWork.BeginTransactionAsync(cancellationToken);
        // Lo que la ingesta encoló para el agente mientras volvía pasa al chatbot (el procesador de Ai ya no lo atendería). La fila
        // AgentReturned va justo delante del más viejo de esos entrantes: la cola atiende por hora, y la corrida tiene que salir del bloque
        // antes de leerlos como respuesta (con la corrida todavía delegada, el motor los saltaría).
        var oldest = await services.Inbox.ReassignPendingEngineAsync(tenantId, conversationId, BotKind.Chatbot, null, cancellationToken);
        var queuedAt = oldest is { } first && first <= now ? first.AddTicks(-10) : now;
        services.Inbox.EnqueueAgentReturned(tenantId, conversation.ConnectionId, conversationId, delegationId, queuedAt);
        services.Events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
        services.Audit.RecordBySystem(tenantId, MessagingAuditActions.AutomationReturned, conversation.Id, now);
        // Spec 2026-11-17 §6.3: resultado, conversación, filas de la cola, evento y auditoría en una sola transacción.
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(tenantId, conversationId, () => services.Notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken));
    }

    public async Task InterruptOrphanDelegationAsync(Guid tenantId, Guid conversationId, Guid delegationId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = new Services(scope.ServiceProvider);
        var delegation = await services.Delegations.FindAsync(tenantId, delegationId, cancellationToken);
        if (delegation is null || delegation.ConversationId != conversationId)
        {
            return;
        }

        // Se vuelve a mirar en Mensajería: la conversación pudo volver a apuntarla entre la lectura de Ai y ésta.
        var conversation = await scope.ServiceProvider.GetRequiredService<IConversationRepository>().FindAsync(tenantId, conversationId, cancellationToken);
        var now = services.Clock.UtcNow;
        if (conversation?.AutomationDelegationId == delegationId || !delegation.Interrupt(now))
        {
            return;
        }

        // La corrida que delegó espera en el bloque y el barrido de abandonadas la salta (§6.5): la señal la cierra si nadie la atiende.
        services.Inbox.EnqueueReleaseRun(tenantId, delegation.ConnectionId, conversationId, BotKind.Chatbot, now);
        await services.UnitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>La salida del resultado para el evento del hilo; Mensajería no interpreta el resto del JSON. Sin <c>output</c> (o
    /// <c>null</c>) es un retorno sin salida (§6.4).</summary>
    private static string? OutputOf(string resultJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(resultJson);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == System.Text.Json.JsonValueKind.String
                ? output.GetString()
                : null;
    }

    /// <summary>C-15: lo que no es texto viaja al modelo como un marcador fijo; visión queda fuera.</summary>
    private static AgentTurnKind KindOf(MessageKind kind) => kind switch
    {
        MessageKind.Text or MessageKind.Interactive or MessageKind.Template => AgentTurnKind.Text,
        MessageKind.Image => AgentTurnKind.Image,
        MessageKind.Audio => AgentTurnKind.Audio,
        MessageKind.Video => AgentTurnKind.Video,
        MessageKind.Document => AgentTurnKind.Document,
        MessageKind.Sticker => AgentTurnKind.Sticker,
        MessageKind.Location => AgentTurnKind.Location,
        MessageKind.Contacts => AgentTurnKind.Contact,
        _ => AgentTurnKind.Other,
    };

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

    private sealed record MessageTextRow(Guid Id, short Kind, string? Text);

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
    }
}

/// <summary>Spec 2026-10-29 §6.1: la cola de los agentes es la de Mensajería, filtrada por <c>bot_kind = 2</c>. El testigo <c>attempts</c>
/// pasa tal cual (P2-R3).</summary>
internal sealed class AiInbox(IAutomationInbox inbox) : IAiInbox
{
    public async Task<IReadOnlyList<AiInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken) =>
    [
        .. (await inbox.ClaimAsync(now, batch, BotKind.Agent, cancellationToken)).Select(item => new AiInboxItem(
            item.MessageId, item.TenantId, item.ConnectionId, item.ConversationId, item.Opened, item.CreatedAt, item.Attempts,
            ReleaseRun: item.Kind == AutomationInboxKinds.ReleaseRun,
            DelegationId: item.Kind == AutomationInboxKinds.Delegated ? item.DelegationId : null)),
    ];

    public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken) =>
        inbox.CompleteAsync(tenantId, messageId, attempts, outcome, cancellationToken);

    public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken) =>
        inbox.RetryLaterAsync(tenantId, messageId, attempts, delay, cancellationToken);

    public async Task<bool> IsInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        await inbox.ReadInboundAsync(tenantId, messageId, cancellationToken) is not null;
}

/// <summary>Spec 2026-10-29 §6.5: el envío del agente es el del bot de Mensajería con <c>BotRef(Agent, id)</c>.</summary>
internal sealed class AiBotOutbound(IBotOutbound outbound) : IAiBotOutbound
{
    public async Task<AiSendResult> SendTextAsync(
        Guid tenantId, Guid conversationId, Guid agentId, Guid clientId, string text, CancellationToken cancellationToken) =>
        await outbound.SendAsync(new BotSend(tenantId, conversationId, BotRef.Agent(agentId), clientId, text, null), cancellationToken) switch
        {
            BotSendResult.Sent sent => new AiSendResult(AiSendStatus.Sent, sent.MessageId),
            BotSendResult.NotAutomated => new AiSendResult(AiSendStatus.NotAutomated),
            BotSendResult.WindowClosed => new AiSendResult(AiSendStatus.WindowClosed),
            BotSendResult.Blocked => new AiSendResult(AiSendStatus.Blocked),
            BotSendResult.ConnectionUnavailable unavailable =>
                new AiSendResult(AiSendStatus.ConnectionUnavailable, Code: unavailable.Reason, ConnectionName: unavailable.ConnectionName),
            BotSendResult.Rejected rejected => new AiSendResult(AiSendStatus.Rejected, Code: rejected.Code),
            var other => throw new InvalidOperationException($"Unknown bot send result {other.GetType().Name}."),
        };
}

/// <summary>D-A9: el horario efectivo de la conexión, la misma cadena que la bandeja y el bloque Horario de los chatbots.</summary>
internal sealed class AiBusinessHours(IBusinessHours businessHours) : IAiBusinessHours
{
    public async Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await businessHours.GetStatusAsync(tenantId, connectionId, cancellationToken)).IsOpen;
}
