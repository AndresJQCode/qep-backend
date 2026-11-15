using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Los tiempos del procesador de entrantes y del worker de modelo (§6.2, §6.3), de <c>Ai:Workers</c>.</summary>
/// <param name="Debounce">D-A14: cuánto espera un trabajo nuevo para juntar una ráfaga del cliente.</param>
/// <param name="IdleAfter">D-A19: una sesión sin entrante en este tiempo está abandonada.</param>
public sealed record AiSessionTimings(TimeSpan Debounce, TimeSpan IdleAfter)
{
    public static AiSessionTimings Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromHours(24));
}

/// <summary>D-A15: la misma regla que <c>BotAttendance.HumanTookOver</c> de Automations, para un agente.</summary>
public static class AgentAttendance
{
    /// <summary>La conversación ya no es de esta sesión: la tomó o resolvió una persona, la pausó, la tiene otro bot, o una persona la
    /// devolvió al asistente después de que la sesión empezó (el ida y vuelta no deja otro rastro, P2-R25).</summary>
    public static bool HumanTookOver(AiConversationState conversation, AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(session);
        return !conversation.IsAttendedBy(session.AgentId) || conversation.AutomationResumedAt > session.StartedAt;
    }
}

/// <summary>
/// Spec 2026-10-29 §6.2 (etapa 1, D-A13): una fila <c>bot_kind = 2</c> de la cola, en una unidad corta. Con la transacción de Ai y el
/// candado de la conversación (el mismo de Automations), decide si el agente atiende, abre o continúa la sesión, anota el turno del cliente y
/// encola el trabajo de modelo. Nunca llama al modelo. Lo que toca en Mensajería lo confirma Mensajería en el momento; un reintento lo
/// encuentra hecho y lo tiene en cuenta (la sesión nueva usa el id que deriva del entrante y el saludo su <c>clientId</c>).
/// <para>Plan 2026-11-12 C-19: antes de abrir una sesión mira la cuenta <b>del proveedor del agente</b> de la conexión; sin ella, o con la
/// conexión pausada o por revisar, no abre nada y la conversación queda en la bandeja como si no hubiera bot. La cuenta del otro proveedor
/// no cuenta.</para>
/// </summary>
public sealed partial class AiInboxProcessor(
    IAiSessionUnitOfWork unitOfWork,
    IAgentSessionRepository sessions,
    IAiModelJobs jobs,
    IAiInbox inbox,
    IAiConversations conversations,
    IAiBusinessHours businessHours,
    IAiModuleStatus modules,
    IAiProviderCredentials credentials,
    IAiBotOutbound outbound,
    AiSessionTimings timings,
    IClock clock,
    ILogger<AiInboxProcessor> logger)
{
    private static readonly TimeSpan ConcurrencyRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LockedRetryDelay = TimeSpan.FromSeconds(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI inbox row {MessageId} of tenant {TenantId} was re-claimed after attempt {Attempts}; this result does not count.")]
    private static partial void LogReclaimed(ILogger logger, Guid tenantId, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI agent {AgentId} of tenant {TenantId} skipped conversation {ConversationId}: its {Provider} account is unavailable ({Reason}).")]
    private static partial void LogProviderUnavailable(ILogger logger, Guid tenantId, Guid agentId, Guid conversationId, string provider, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI agent {AgentId} of tenant {TenantId} has a published version that cannot be read; it does not take conversation {ConversationId}.")]
    private static partial void LogUnreadableVersion(ILogger logger, Guid tenantId, Guid agentId, Guid conversationId);

    public async Task<string> ProcessAsync(AiInboxItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        string outcome;
        try
        {
            outcome = await ProcessUnitAsync(item, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            return await RetryLaterAsync(item, ConcurrencyRetryDelay, AiInboxOutcomes.Retry);
        }

        if (outcome == AiInboxOutcomes.Locked)
        {
            return await RetryLaterAsync(item, LockedRetryDelay, AiInboxOutcomes.Locked);
        }

        // Sin cancelación: la unidad ya se confirmó; una fila abierta se volvería a procesar como un entrante nuevo.
        if (!await inbox.CompleteAsync(item.TenantId, item.MessageId, item.Attempts, outcome, CancellationToken.None))
        {
            LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
        }

        return outcome;
    }

    private async Task<string> RetryLaterAsync(AiInboxItem item, TimeSpan delay, string outcome)
    {
        if (!await inbox.RetryLaterAsync(item.TenantId, item.MessageId, item.Attempts, delay, CancellationToken.None))
        {
            LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
        }

        return outcome;
    }

    private async Task<string> ProcessUnitAsync(AiInboxItem item, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(item.ConversationId, cancellationToken))
        {
            return AiInboxOutcomes.Locked;
        }

        var session = await sessions.FindLiveForUpdateAsync(item.TenantId, item.ConversationId, cancellationToken);
        // P2-R24: esta fila ya se procesó (el cierre falló o el lease venció antes): nada se repite.
        var latest = session ?? await sessions.FindLatestAsync(item.TenantId, item.ConversationId, cancellationToken);
        if (latest?.LastInboundMessageId == item.MessageId)
        {
            return AiInboxOutcomes.Duplicate;
        }

        var conversation = await conversations.GetAsync(item.TenantId, item.ConversationId, cancellationToken);
        var now = conversation?.AutomationResumedAt is { } resumedAt && resumedAt > clock.UtcNow ? resumedAt : clock.UtcNow;

        // §6.8: con el módulo apagado el agente no contesta; la conversación que atendía pasa a la bandeja sin asignar, con aviso.
        if (!await modules.IsEnabledAsync(item.TenantId, cancellationToken))
        {
            await StandDownAsync(item, conversation, session, now, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return AiInboxOutcomes.ModuleDisabled;
        }

        var interrupted = false;
        if (session is not null && (conversation is null || AgentAttendance.HumanTookOver(conversation, session)))
        {
            // D-A15: una persona intervino; el entrante ya no es del agente.
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, now);
            session.MarkInbound(item.MessageId);
            session = null;
            interrupted = true;
        }

        string outcome;
        if (conversation is null || item.ReleaseRun || !await inbox.IsInboundAsync(item.TenantId, item.MessageId, cancellationToken))
        {
            outcome = interrupted ? AiInboxOutcomes.Interrupted : AiInboxOutcomes.Skipped;
        }
        else if (session is not null)
        {
            await AppendCustomerTurnAsync(session, item, now, cancellationToken);
            outcome = AiInboxOutcomes.Continued;
        }
        else
        {
            outcome = await TryOpenAsync(item, conversation, now, cancellationToken) ?? (interrupted ? AiInboxOutcomes.Interrupted : AiInboxOutcomes.Skipped);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    /// <summary>§6.2 paso 6: sin sesión viva. En <c>None</c> el agente <c>Active</c> de la conexión toma la conversación si su disparador
    /// aplica (D-A9); en <c>Active</c> de un agente (una persona se la devolvió) se abre otra sesión sin disparador. <c>null</c> = nadie.</summary>
    private async Task<string?> TryOpenAsync(AiInboxItem item, AiConversationState conversation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (conversation.Status != AiConversationState.Open || conversation.AssignedMemberId is not null || conversation.Blocked)
        {
            return null;
        }

        Agent? agent;
        var returned = conversation.AutomationStatus == AiConversationState.Active;
        if (returned)
        {
            if (conversation.AutomationBotKind != AiConversationState.AgentBot || conversation.AutomationAgentId is not { } attending)
            {
                return null;
            }

            agent = await sessions.FindAgentAsync(item.TenantId, attending, cancellationToken);
        }
        else
        {
            agent = await sessions.FindServingAgentAsync(item.TenantId, item.ConnectionId, cancellationToken);
            if (agent is null || !await TriggerAppliesAsync(agent, item, cancellationToken))
            {
                return null;
            }
        }

        if (agent?.PublishedVersion is not { } version || !AiProvider.TryParse(version.Provider, out var provider))
        {
            if (agent is not null)
            {
                LogUnreadableVersion(logger, item.TenantId, agent.Id, item.ConversationId);
            }

            return null;
        }

        // C-19: la cuenta del proveedor de este agente, no la del tenant ni la del otro proveedor.
        var credential = await credentials.ResolveAsync(item.TenantId, provider!, cancellationToken);
        if (credential.Credential is null)
        {
            var providerKey = provider!.Value;
            var reason = credential.Unavailable is { } unavailable ? unavailable.ToString() : "unknown";
            LogProviderUnavailable(logger, item.TenantId, agent.Id, item.ConversationId, providerKey, reason);
            return AiInboxOutcomes.ProviderUnavailable;
        }

        if (!returned)
        {
            try
            {
                await conversations.StartAsync(item.TenantId, item.ConversationId, agent.Id, cancellationToken);
            }
            catch (DomainException)
            {
                // Una persona la tomó entre la lectura y el arranque: queda para ella.
                return null;
            }
        }

        var session = AgentSession.Open(
            AiDeterministicIds.SessionFor(item.MessageId), item.TenantId, item.ConversationId, item.ConnectionId, agent.Id, version.Id, now);
        sessions.Add(session);

        // §6.2 paso 6: el saludo se envía tal cual, sin pasar por el modelo, en esta misma unidad.
        var config = AgentConfig.FromJson(version.ConfigJson);
        if (!string.IsNullOrWhiteSpace(config.Greeting))
        {
            var greeting = await outbound.SendTextAsync(
                item.TenantId, item.ConversationId, agent.Id, AiDeterministicIds.GreetingClientId(session.Id), config.Greeting.Trim(), cancellationToken);
            if (greeting is { Status: AiSendStatus.Sent, MessageId: { } greetingId })
            {
                sessions.AddTurn(session.AppendTurn(AgentTurnRoleKind.Greeting, greetingId, null, [], truncated: false, now));
            }
        }

        await AppendCustomerTurnAsync(session, item, now, cancellationToken);
        return AiInboxOutcomes.Started;
    }

    /// <summary>D-A9 y D-M16: sólo el entrante que abre o reabre la conversación arranca un agente; con <c>OutsideHours</c>, además, fuera
    /// del horario de la conexión.</summary>
    private async Task<bool> TriggerAppliesAsync(Agent agent, AiInboxItem item, CancellationToken cancellationToken)
    {
        if (!item.Opened || agent.PublishedVersion is not { } version)
        {
            return false;
        }

        return AgentConfig.FromJson(version.ConfigJson).Trigger switch
        {
            "OutsideHours" => !await businessHours.IsOpenAsync(item.TenantId, item.ConnectionId, cancellationToken),
            _ => true,
        };
    }

    /// <summary>§6.2 paso 7 y D-A14: el turno del cliente y el trabajo. Con uno pendiente sólo se le suben los turnos; si no, uno nuevo con el
    /// debounce para juntar la ráfaga.</summary>
    private async Task AppendCustomerTurnAsync(AgentSession session, AiInboxItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var turn = session.AppendTurn(AgentTurnRoleKind.Customer, item.MessageId, null, [], truncated: false, now);
        sessions.AddTurn(turn);
        session.MarkInbound(item.MessageId);
        if (await sessions.FindPendingJobAsync(session.Id, cancellationToken) is { } pending)
        {
            pending.ExtendTo(turn.Seq);
            return;
        }

        jobs.Add(ModelJob.ForSession(Guid.CreateVersion7(), item.TenantId, session.Id, turn.Seq, now + timings.Debounce, now));
    }

    private async Task StandDownAsync(
        AiInboxItem item, AiConversationState? conversation, AgentSession? session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return;
        }

        session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.ModuleDisabled, null, now);
        session.MarkInbound(item.MessageId);
        await AiHandoffs.ModuleDisabledAsync(conversations, sessions, item.TenantId, conversation, session, cancellationToken);
    }
}

/// <summary>Los traspasos que comparten el procesador, el worker y los barridos.</summary>
public static class AiHandoffs
{
    /// <summary>§6.8: la conversación que el agente atendía pasa a la bandeja sin asignar, con aviso. Una que ya no es suya queda como está.</summary>
    public static async Task ModuleDisabledAsync(
        IAiConversations conversations, IAgentSessionRepository sessions, Guid tenantId, AiConversationState? conversation, AgentSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(session);
        if (conversation is null || !conversation.IsAttendedBy(session.AgentId))
        {
            return;
        }

        var agent = await sessions.FindAgentAsync(tenantId, session.AgentId, cancellationToken);
        var provider = agent?.Versions.FirstOrDefault(version => version.Id == session.AgentVersionId)?.Provider;
        try
        {
            await conversations.HandoffAsync(
                tenantId, session.ConversationId, new AgentHandoffCause(HandoffCauseKind.ModuleDisabled, null, provider), null, cancellationToken);
        }
        catch (DomainException)
        {
            // Una persona la tomó en el medio.
        }
    }
}
