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
        return !conversation.IsAttendedBy(session.AgentId) || conversation.AutomationResumedAt > session.StartedAt
            // Spec 2026-11-17: la sesión de un bloque es de su delegación; otra (o ninguna) es que la conversación siguió sin ella.
            || (session.IsBlock && conversation.AutomationDelegationId != session.DelegationId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI agent {AgentId} of tenant {TenantId} cannot take conversation {ConversationId} back (paused, deleted or without a readable version); it is handed off.")]
    private static partial void LogCannotTakeBack(ILogger logger, Guid tenantId, Guid agentId, Guid conversationId);

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

        // Spec 2026-11-17 §6.2: la fila Delegated no es un entrante (su message_id no es un mensaje): abre la sesión del bloque y nada más.
        // Sin esta rama se leería como un arranque manual.
        if (item.DelegationId is { } delegationId)
        {
            var conversationNow = await conversations.GetAsync(item.TenantId, item.ConversationId, cancellationToken);
            // Revisión de la rama, hallazgo 2: la delegación viva sola no alcanza. Si la conversación ya no la apunta (la reemplazó otra, o un
            // cambio limpió la columna sin cerrarla) lo que el agente conteste no le llega a nadie y la corrida espera para siempre.
            if (conversationNow?.AutomationDelegationId != delegationId)
            {
                await conversations.InterruptOrphanDelegationAsync(item.TenantId, item.ConversationId, delegationId, cancellationToken);
                return AiInboxOutcomes.Skipped;
            }

            var opened = await OpenBlockAsync(item, delegationId, inbound: null, NowFor(conversationNow), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return opened;
        }

        var session = await sessions.FindLiveForUpdateAsync(item.TenantId, item.ConversationId, cancellationToken);
        // P2-R24: esta fila ya se procesó (el cierre falló o el lease venció antes): nada se repite.
        var latest = session ?? await sessions.FindLatestAsync(item.TenantId, item.ConversationId, cancellationToken);
        if (latest?.LastInboundMessageId == item.MessageId)
        {
            return AiInboxOutcomes.Duplicate;
        }

        var conversation = await conversations.GetAsync(item.TenantId, item.ConversationId, cancellationToken);
        var now = NowFor(conversation);

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
            // D-A15: una persona intervino; el entrante ya no es del agente. Es también lo que hace la señal ReleaseRun (tomar, transferir,
            // resolver, devolver): el trabajo pendiente se descarta aquí, sin esperar a que el worker lo reclame.
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, now);
            session.MarkInbound(item.MessageId);
            if (await sessions.FindPendingJobAsync(session.Id, cancellationToken) is { } pending)
            {
                pending.Complete(ModelJobStatus.Discarded, AgentSessionEndReasons.HumanTookOver, null, now);
            }
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
            await AppendCustomerTurnAsync(session, item.MessageId, now, cancellationToken);
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

        // Spec 2026-11-17 §6.2: la conversación es de un agente por un bloque de chatbot y la fila Delegated todavía no abrió la sesión (un
        // reintento por candado la dejó atrás). Este entrante abre la misma sesión del bloque, sin saludo ni disparador; nunca una de conexión.
        if (conversation.AutomationDelegationId is { } delegationId)
        {
            return await OpenBlockAsync(item, delegationId, inbound: item, now, cancellationToken);
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

            // Revisión final, hallazgo 4: la conversación ya es del agente (Active), así que no traspasar la dejaba asignada a un agente que
            // nunca contesta. Un agente pausado no abre sesiones nuevas (D-A6: sólo terminan las vivas); uno borrado o sin versión que se pueda
            // leer, tampoco. La conversación pasa a una persona.
            if (agent is not { Status: AgentStatus.Active, PublishedVersion: { } readable } || !AiProvider.TryParse(readable.Provider, out _))
            {
                LogCannotTakeBack(logger, item.TenantId, attending, item.ConversationId);
                await HandOffAsync(
                    item, new AgentHandoffCause(HandoffCauseKind.AgentFailed, null, agent?.PublishedVersion?.Provider), null, cancellationToken);
                return AiInboxOutcomes.HandedOff;
            }
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
            if (returned)
            {
                // Revisión final, hallazgo 4: en None la conversación queda en la bandeja como si no hubiera bot (C-19); en Active ya es del
                // agente y sin traspaso nadie la vería.
                await HandOffAsync(
                    item, new AgentHandoffCause(HandoffCauseKind.ProviderFailed, AiErrorCodes.ProviderNotConfigured, providerKey),
                    DestinationOf(version), cancellationToken);
            }

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

        await AppendCustomerTurnAsync(session, item.MessageId, now, cancellationToken);
        return AiInboxOutcomes.Started;
    }

    /// <summary>
    /// Spec 2026-11-17 §6.2: abre la sesión del bloque de la delegación, con el candado ya tomado. El id sale de la delegación, así que la
    /// fila <c>Delegated</c> y un entrante que se le adelante abren la misma (el segundo es <c>duplicate</c>). Sin saludo (D-B6) y sin mirar
    /// ocupante ni disparador: la conversación ya es del agente. Si el agente no puede atender (pausado, borrado, sin versión legible, sin la
    /// cuenta de su proveedor, módulo apagado) no abre nada y la conversación vuelve al flujo por <c>error</c>.
    /// </summary>
    /// <param name="inbound">El entrante que llegó antes que la fila <c>Delegated</c>; <c>null</c> desde la fila.</param>
    private async Task<string> OpenBlockAsync(
        AiInboxItem item, Guid delegationId, AiInboxItem? inbound, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessionId = AiDeterministicIds.SessionForDelegation(delegationId);
        if (await sessions.FindAsync(item.TenantId, sessionId, cancellationToken) is not null)
        {
            return AiInboxOutcomes.Duplicate;
        }

        var delegation = await conversations.ReadDelegationAsync(item.TenantId, delegationId, cancellationToken);
        if (delegation is null || !delegation.IsLive || delegation.ConversationId != item.ConversationId)
        {
            // Una persona la tomó antes (la delegación quedó Interrupted) o ya volvió: nadie espera esta sesión.
            return AiInboxOutcomes.Skipped;
        }

        // D-B16: con el módulo apagado el bloque sale por error y lo decide el flujo, no la bandeja.
        if (!await modules.IsEnabledAsync(item.TenantId, cancellationToken))
        {
            return await ReturnAsync(item, delegationId, AgentBlockReasons.ModuleDisabled, cancellationToken);
        }

        // D-B18 y §6.6: un agente pausado no abre delegaciones nuevas (D-A6); uno borrado o sin versión legible, tampoco.
        var agent = await sessions.FindAgentAsync(item.TenantId, delegation.AgentId, cancellationToken);
        if (agent is not { Status: AgentStatus.Active, PublishedVersion: { } version } || !AiProvider.TryParse(version.Provider, out var provider))
        {
            LogCannotTakeBack(logger, item.TenantId, delegation.AgentId, item.ConversationId);
            return await ReturnAsync(item, delegationId, AgentBlockReasons.AgentUnavailable, cancellationToken);
        }

        // C-19: la cuenta del proveedor de este agente.
        var credential = await credentials.ResolveAsync(item.TenantId, provider!, cancellationToken);
        if (credential.Credential is null)
        {
            LogProviderUnavailable(
                logger, item.TenantId, agent.Id, item.ConversationId, provider!.Value,
                credential.Unavailable is { } unavailable ? unavailable.ToString() : "unknown");
            return await ReturnAsync(item, delegationId, AiErrorCodes.ProviderNotConfigured, cancellationToken);
        }

        // D-B5: la versión publicada de ahora, con la que la sesión termina aunque el agente se republique.
        var brief = AgentBlockBrief.Parse(delegation.BriefJson);
        var session = AgentSession.OpenForBlock(
            sessionId, item.TenantId, item.ConversationId, delegation.ConnectionId, agent.Id, version.Id, delegationId, delegation.ChatbotId,
            delegation.RunId, brief.IdleMinutes, now);
        sessions.Add(session);

        // D-B7: lastMessage responde el entrante que llevó la corrida al bloque; sin él (arranque manual) espera como nextMessage. Un
        // entrante que se adelantó a la fila va detrás, en el mismo trabajo.
        var turns = new List<Guid>(2);
        if (brief.AnswersLastMessage && delegation.LastInboundMessageId is { } reached && reached != inbound?.MessageId)
        {
            turns.Add(reached);
        }

        if (inbound is not null)
        {
            turns.Add(inbound.MessageId);
        }

        await AppendCustomerTurnsAsync(session, turns, now, cancellationToken);
        return AiInboxOutcomes.Started;
    }

    private async Task<string> ReturnAsync(AiInboxItem item, Guid delegationId, string reason, CancellationToken cancellationToken) =>
        await AgentBlockReturn.TryReturnAsync(
            conversations, item.TenantId, item.ConversationId, delegationId, AgentBlockResult.Error(reason), cancellationToken)
            ? AiInboxOutcomes.Returned
            : AiInboxOutcomes.Skipped;

    /// <summary>El reloj de la unidad: si una persona devolvió la conversación al asistente con un reloj adelantado, la sesión empieza
    /// después (P2-R25).</summary>
    private DateTimeOffset NowFor(AiConversationState? conversation) =>
        conversation?.AutomationResumedAt is { } resumedAt && resumedAt > clock.UtcNow ? resumedAt : clock.UtcNow;

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
    private Task AppendCustomerTurnAsync(AgentSession session, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        AppendCustomerTurnsAsync(session, [messageId], now, cancellationToken);

    /// <summary>Varios turnos del cliente en la misma unidad, con un solo trabajo: el pendiente nuevo todavía no está en la base y la lectura
    /// con <c>FOR UPDATE</c> no lo vería (<c>IX_model_jobs_session_open</c>).</summary>
    private async Task AppendCustomerTurnsAsync(
        AgentSession session, IReadOnlyList<Guid> messageIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AgentTurn? turn = null;
        foreach (var messageId in messageIds)
        {
            turn = session.AppendTurn(AgentTurnRoleKind.Customer, messageId, null, [], truncated: false, now);
            sessions.AddTurn(turn);
            session.MarkInbound(messageId);
        }

        if (turn is null)
        {
            return;
        }

        if (await sessions.FindPendingJobAsync(session.Id, cancellationToken) is { } pending)
        {
            pending.ExtendTo(turn.Seq);
            return;
        }

        jobs.Add(ModelJob.ForSession(Guid.CreateVersion7(), session.TenantId, session.Id, turn.Seq, now + timings.Debounce, now));
    }

    /// <summary>Revisión final, hallazgo 4: el traspaso de una conversación que el agente tiene sin sesión viva. Si una persona la tomó en el
    /// medio, ya tiene humano.</summary>
    private async Task HandOffAsync(AiInboxItem item, AgentHandoffCause cause, Guid? assignTo, CancellationToken cancellationToken)
    {
        try
        {
            await conversations.HandoffAsync(item.TenantId, item.ConversationId, cause, assignTo, cancellationToken);
        }
        catch (DomainException)
        {
            // Una persona la tomó entre la lectura y el traspaso.
        }
    }

    private static Guid? DestinationOf(AgentVersion version)
    {
        var handoff = AgentConfig.FromJson(version.ConfigJson).Handoff;
        return handoff.Destination == AgentHandoff.ToMember ? handoff.MemberId : null;
    }

    private async Task StandDownAsync(
        AiInboxItem item, AiConversationState? conversation, AgentSession? session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            // Spec 2026-11-17 D-B16: una delegación cuya sesión todavía no abrió vuelve al flujo por error, no a la bandeja.
            if (conversation?.AutomationDelegationId is { } delegationId)
            {
                await AgentBlockReturn.TryReturnAsync(
                    conversations, item.TenantId, item.ConversationId, delegationId, AgentBlockResult.Error(AgentBlockReasons.ModuleDisabled),
                    cancellationToken);
                return;
            }

            // Revisión final, hallazgo 4: sin sesión viva, una conversación que el agente tiene (una persona se la devolvió) también pasa a
            // la bandeja; si no, quedaba asignada a un agente que con el módulo apagado nunca contesta.
            if (conversation?.AutomationAgentId is { } agentId && conversation.IsAttendedBy(agentId))
            {
                var agent = await sessions.FindAgentAsync(item.TenantId, agentId, cancellationToken);
                await HandOffAsync(
                    item, new AgentHandoffCause(HandoffCauseKind.ModuleDisabled, null, agent?.PublishedVersion?.Provider), null, cancellationToken);
            }

            return;
        }

        session.MarkInbound(item.MessageId);
        await AiHandoffs.EndForDisabledModuleAsync(conversations, sessions, item.TenantId, conversation, session, now, cancellationToken);
    }
}

/// <summary>Los traspasos que comparten el procesador, el worker y los barridos.</summary>
public static class AiHandoffs
{
    /// <summary>§6.8 y spec 2026-11-17 D-B16: el módulo <c>ai</c> se apagó con la sesión viva. La de una conexión termina <c>Interrupted</c> y
    /// su conversación pasa a la bandeja; la de un bloque devuelve la conversación al flujo por <c>error</c>, que decide qué sigue.</summary>
    public static async Task EndForDisabledModuleAsync(
        IAiConversations conversations, IAgentSessionRepository sessions, Guid tenantId, AiConversationState? conversation, AgentSession session,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsBlock)
        {
            await AgentBlockReturn.EndAsync(conversations, tenantId, session, AgentBlockResult.Error(AgentBlockReasons.ModuleDisabled), now, cancellationToken);
            return;
        }

        session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.ModuleDisabled, null, now);
        await ModuleDisabledAsync(conversations, sessions, tenantId, conversation, session, cancellationToken);
    }

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
