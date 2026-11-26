using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-10-29 §6.3 (etapa 2, D-A13): corre el trabajo de modelo de una sesión real. Primero, <b>fuera de toda transacción y sin el
/// candado</b>, arma el contexto con el proveedor y la versión <b>de la sesión</b> (C-17) y corre el <see cref="AgentTurnRunner"/>; después,
/// en un commit corto con el candado de la conversación, vuelve a mirar que la conversación siga siendo del agente y envía (§6.3 paso 4).
/// <list type="bullet">
/// <item>D-A15: si una persona intervino mientras el modelo pensaba, no se envía nada; la sesión termina <c>Interrupted</c> y el consumo
/// queda registrado (los tokens ya se gastaron).</item>
/// <item>D-A14: si el cliente escribió después, el trabajo se descarta (hasta dos veces seguidas): el siguiente responde todo junto.</item>
/// <item>D-A18: con la ventana del canal cerrada no se envía; traspaso <c>WindowClosed</c> para que una persona decida con plantilla.</item>
/// <item>Todo error del proveedor o del agente termina en traspaso (Review Focus 6): el cliente nunca se queda sin respuesta y sin humano.
/// Sin respaldo entre proveedores (C-20).</item>
/// </list>
/// </summary>
public sealed partial class AgentSessionJobProcessor(
    IAiSessionUnitOfWork unitOfWork,
    IAgentSessionRepository sessions,
    IAiModelJobs jobs,
    IAiConversations conversations,
    IAiBotOutbound outbound,
    IAiModuleStatus modules,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    IAiQuickReplies quickReplies,
    ITenantClock tenantClock,
    AgentPromptBuilder promptBuilder,
    AgentTurnRunner runner,
    IClock clock,
    ILogger<AgentSessionJobProcessor> logger,
    IAiAuditRecorder audit)
{
    /// <summary>§6.3 paso 4: el candado estaba tomado; la respuesta guardada se reintenta en 2 s sin llamar al modelo otra vez.</summary>
    internal static readonly TimeSpan LockedRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>El <c>error_code</c> de un trabajo que espera el candado de la conversación.</summary>
    private const string LockedErrorCode = "conversation_locked";

    /// <summary>§6.3: una respuesta que pasa el tope del canal se parte en hasta tres mensajes.</summary>
    public const int MaxReplyParts = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI job {JobId} of tenant {TenantId} closed as {Outcome}.")]
    private static partial void LogClosed(ILogger logger, Guid tenantId, Guid jobId, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI job {JobId} of tenant {TenantId} rescheduled after {Code} (attempt {Attempts}).")]
    private static partial void LogRescheduled(ILogger logger, Guid tenantId, Guid jobId, string code, int attempts);

    public async Task ProcessAsync(ModelJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var session = job.SessionId is { } sessionId ? await sessions.FindAsync(job.TenantId, sessionId, cancellationToken) : null;
        if (session is null || !session.IsLive)
        {
            await CloseAsync(job, ModelJobStatus.Discarded, "session_ended", null);
            return;
        }

        var agent = await sessions.FindAgentAsync(job.TenantId, session.AgentId, cancellationToken);
        var version = agent?.Versions.FirstOrDefault(candidate => candidate.Id == session.AgentVersionId);
        if (version is null)
        {
            // Revisión final, hallazgo 4: el agente (o su versión) se borró con la sesión viva. No hay con qué responder, y descartar en
            // silencio dejaba la conversación asignada a un agente que no existe.
            await GiveUpAsync(job, session.ConversationId, new AgentHandoffCause(HandoffCauseKind.AgentFailed, null, null), null, cancellationToken);
            return;
        }

        var config = AgentConfig.FromJson(version.ConfigJson);
        if (agent is { Status: AgentStatus.Paused })
        {
            // Owner, 2026-11-18 (revierte D-A6): pausar corta también las sesiones vivas. Sin llamar al modelo, la conversación pasa a una
            // persona; si no, el agente pausado seguía contestando mientras la sesión estuviera viva.
            await GiveUpAsync(
                job, session.ConversationId, new AgentHandoffCause(HandoffCauseKind.AgentFailed, AgentHandoffCodes.AgentPaused, version.Provider),
                DestinationOf(config.Handoff), cancellationToken);
            return;
        }

        if (job.Attempts > ModelJob.MaxClaims)
        {
            // Revisión final, hallazgo 2: un trabajo que se reclamó demasiadas veces no se retoma más; una persona sigue.
            await GiveUpAsync(
                job, session.ConversationId, new AgentHandoffCause(HandoffCauseKind.AgentFailed, AgentHandoffCodes.JobTimedOut, version.Provider),
                DestinationOf(config.Handoff), cancellationToken);
            return;
        }

        AgentTurnOutcome outcome;
        if (job.ResultJson is { } held)
        {
            outcome = StoredOutcome.Read(held);
        }
        else
        {
            var computed = await RunAsync(job, session, version, config, cancellationToken);
            if (computed is null)
            {
                // Reprogramado por un error transitorio (D-A17), o descartado antes de llamar al modelo.
                return;
            }

            // Revisión final, hallazgo 2: guardada antes de enviar nada. Un commit que se deshace o un proceso que muere retoman con ella y el
            // modelo (pagado) no se llama otra vez. Sin cancelación: los tokens ya se gastaron.
            outcome = computed;
            job.RememberResult(StoredOutcome.Write(outcome));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            await CommitAsync(job, session.ConversationId, version, config, outcome, cancellationToken);
        }
        catch (RequestConcurrencyException exception)
        {
            // La conversación cambió en Mensajería mientras se confirmaba (un entrante que llegó a la vez, P2: conversations.version), o la
            // sesión cambió en Ai. La transacción se deshizo; el trabajo se retoma pronto con la respuesta guardada.
            await jobs.RetryHeldAsync(job.Id, clock.UtcNow + LockedRetryDelay, CancellationToken.None);
            LogRescheduled(logger, job.TenantId, job.Id, exception.Code, job.Attempts);
        }
    }

    private static Guid? DestinationOf(AgentHandoff handoff) => handoff.Destination == AgentHandoff.ToMember ? handoff.MemberId : null;

    /// <summary>Cierra el trabajo con traspaso sin llamar al modelo (un agente que ya no existe, un trabajo que se reclamó demasiadas veces).
    /// Con el candado de la conversación, como el commit corto; ocupado, se reintenta en 2 s.</summary>
    private async Task GiveUpAsync(
        ModelJob job, Guid conversationId, AgentHandoffCause cause, Guid? assignTo, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(conversationId, cancellationToken))
        {
            job.RetryAt(clock.UtcNow + LockedRetryDelay, LockedErrorCode);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var session = await sessions.FindLiveForUpdateAsync(job.TenantId, conversationId, cancellationToken);
        if (session is null || session.Id != job.SessionId)
        {
            await CloseAsync(job, ModelJobStatus.Discarded, "session_ended", null);
            return;
        }

        // Spec 2026-11-17 §6.3: la sesión de un bloque no traspasa; vuelve al flujo por error.
        if (session.IsBlock)
        {
            await AgentBlockReturn.EndAsync(conversations, job.TenantId, session, AgentBlockReturn.For(cause), clock.UtcNow, cancellationToken);
            await CloseAsync(job, ModelJobStatus.Failed, "agent_failed", cause.Code);
            return;
        }

        var conversation = await conversations.GetAsync(job.TenantId, conversationId, cancellationToken);
        if (conversation is not null && conversation.IsAttendedBy(session.AgentId))
        {
            try
            {
                await conversations.HandoffAsync(job.TenantId, conversationId, cause, assignTo, cancellationToken);
            }
            catch (DomainException)
            {
                // Una persona la tomó en el medio: ya tiene humano.
            }
        }

        session.Finish(AgentSessionStatus.Failed, cause.Kind.ToString(), cause.Code, clock.UtcNow);
        await CloseAsync(job, ModelJobStatus.Failed, "agent_failed", cause.Code);
    }

    /// <summary>La etapa larga: sin transacción ni candado. <c>null</c> si el trabajo quedó reprogramado, o descartado porque la conversación
    /// ya no es del agente.</summary>
    private async Task<AgentTurnOutcome?> RunAsync(
        ModelJob job, AgentSession session, AgentVersion version, AgentConfig config, CancellationToken cancellationToken)
    {
        if (!AiProvider.TryParse(version.Provider, out var provider))
        {
            return new AgentTurnFailed(AgentFailureKind.AgentFailed, AgentFailureReason.ProviderError, AiModelErrorCodes.ModelUnavailable);
        }

        var credential = (await credentials.ResolveAsync(job.TenantId, provider!, cancellationToken)).Credential;
        if (credential is null)
        {
            return new AgentTurnFailed(AgentFailureKind.ProviderFailed, AgentFailureReason.ProviderError, AiErrorCodes.ProviderNotConfigured);
        }

        var conversation = await conversations.GetAsync(job.TenantId, session.ConversationId, cancellationToken);
        if (conversation is null || AgentAttendance.HumanTookOver(conversation, session))
        {
            // Revisión final, hallazgo 5: la conversación ya no es del agente (una persona la tomó, la resolvió o la devolvió). Se descarta antes
            // de pagar el modelo; la sesión la cierra la señal ReleaseRun o el siguiente entrante, con el candado.
            await CloseAsync(job, ModelJobStatus.Discarded, AgentSessionEndReasons.HumanTookOver, null);
            return null;
        }

        var turns = await sessions.ListTurnsAsync(session.Id, job.UpToSeq, cancellationToken);
        var messages = await conversations.ReadMessagesAsync(
            job.TenantId, [.. turns.Select(turn => turn.MessageId).OfType<Guid>()], cancellationToken);
        var history = turns
            .Where(turn => turn.MessageId is { } id && messages.ContainsKey(id))
            .Select(turn => new AgentHistoryTurn(RoleOf(turn.Role), messages[turn.MessageId!.Value].Text, messages[turn.MessageId!.Value].Kind))
            .ToList();

        var replies = config.UseQuickReplies ? await quickReplies.ListAsync(job.TenantId, cancellationToken) : [];
        var block = await BlockOfAsync(job.TenantId, session, cancellationToken);
        var prompt = promptBuilder.Build(new AgentPromptInput(
            config, replies, history, await tenantClock.GetAsync(job.TenantId, cancellationToken), conversation?.WindowRemaining, block));
        var context = new AgentTurnContext(
            job.TenantId,
            session.AgentId,
            version.Id,
            provider!,
            version.Model,
            catalog.Providers.FirstOrDefault(entry => entry.Provider == provider)?.Models.FirstOrDefault(entry => entry.Id == version.Model)?.Effort,
            config,
            credential,
            job.Id,
            session.Id,
            session.ConversationId,
            TestId: null,
            conversation?.CustomerId,
            [.. config.Faq.Select(faq => faq.Id), .. replies.Select(reply => reply.Id)],
            IsTest: false,
            block);

        var outcome = await runner.RunAsync(context, prompt, cancellationToken);
        if (outcome is AgentTurnRetry retry && job.Attempts < ModelJob.MaxAttempts)
        {
            job.RetryAt(clock.UtcNow + (retry.RetryAfter ?? AgentTestJobProcessor.RetryStep * job.Attempts), retry.Code);
            LogRescheduled(logger, job.TenantId, job.Id, retry.Code, job.Attempts);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        return outcome;
    }

    /// <summary>Spec 2026-11-17 D-B8: el objetivo y el contexto del paso, leídos de la delegación de la sesión en cada trabajo (la sesión no
    /// los copia). <c>null</c> fuera de un bloque.</summary>
    private async Task<AgentBlockContext?> BlockOfAsync(Guid tenantId, AgentSession session, CancellationToken cancellationToken)
    {
        if (session.DelegationId is not { } delegationId)
        {
            return null;
        }

        var brief = AgentBlockBrief.Parse((await conversations.ReadDelegationAsync(tenantId, delegationId, cancellationToken))?.BriefJson);
        return new AgentBlockContext(brief.Objective, brief.Context);
    }

    /// <summary>§6.3 paso 4: el commit corto, con el candado de la conversación.</summary>
    private async Task CommitAsync(
        ModelJob job, Guid conversationId, AgentVersion version, AgentConfig config, AgentTurnOutcome outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(conversationId, cancellationToken))
        {
            job.HoldResult(StoredOutcome.Write(outcome), clock.UtcNow + LockedRetryDelay);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = clock.UtcNow;
        var session = await sessions.FindLiveForUpdateAsync(job.TenantId, conversationId, cancellationToken);
        if (session is null || session.Id != job.SessionId)
        {
            await CloseAsync(job, ModelJobStatus.Discarded, "session_ended", null);
            return;
        }

        var conversation = await conversations.GetAsync(job.TenantId, conversationId, cancellationToken);
        if (!await modules.IsEnabledAsync(job.TenantId, cancellationToken))
        {
            await AiHandoffs.EndForDisabledModuleAsync(conversations, sessions, job.TenantId, conversation, session, now, cancellationToken);
            await CloseAsync(job, ModelJobStatus.Discarded, AgentSessionEndReasons.ModuleDisabled, null);
            return;
        }

        if (conversation is null || AgentAttendance.HumanTookOver(conversation, session))
        {
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, now);
            await CloseAsync(job, ModelJobStatus.Discarded, AgentSessionEndReasons.HumanTookOver, null);
            return;
        }

        // El borrador de cotización o la cita ya están escritos: descartar la respuesta dejaría que el siguiente trabajo (otro JobId, otra llave)
        // creara otro borrador o reservara de nuevo.
        var wrote = outcome is AgentReplied { Wrote: true } or AgentHandedOff { Wrote: true } or AgentTurnFailed { Wrote: true } or AgentHandedOff { Reason: AgentHandoffCodes.QuotationDraftCreated };
        if (!wrote && session.CanSupersede && await sessions.HasCustomerTurnAfterAsync(session.Id, job.UpToSeq, cancellationToken))
        {
            session.RecordSuperseded(now);
            await CloseAsync(job, ModelJobStatus.Discarded, "superseded", null);
            return;
        }

        var turn = new TurnCommit(job, session, conversation, version, config, now);
        if (!conversation.WindowOpen)
        {
            await HandOffAsync(turn, new AgentHandoffCause(HandoffCauseKind.WindowClosed, null, version.Provider), AgentSessionStatus.WindowClosed, sendMessage: false, cancellationToken);
            await CloseAsync(job, ModelJobStatus.Done, AgentSessionEndReasons.WindowClosed, null);
            return;
        }

        switch (outcome)
        {
            case AgentReplied replied:
                await ReplyAsync(turn, replied, cancellationToken);
                break;
            case AgentFinished finished when session.IsBlock:
                // D-B8: el objetivo del paso se cumplió; nada sale al cliente y el flujo sigue por done.
                await AgentBlockReturn.EndAsync(
                    conversations, job.TenantId, session,
                    new AgentBlockResult(AgentBlockOutputs.Done, AgentBlockReasons.ObjectiveCompleted, string.IsNullOrEmpty(finished.Summary) ? null : finished.Summary),
                    now, cancellationToken);
                await CloseAsync(job, ModelJobStatus.Done, "returned", null);
                break;
            case AgentHandedOff handedOff:
                if (handedOff.Reason == AgentHandoffCodes.QuotationDraftCreated)
                {
                    // §4.7: ai.quotation_draft.created, en el mismo commit que el traspaso. Quotations audita además la cotización por
                    // integración.
                    audit.RecordByAgent(job.TenantId, AiAuditActions.QuotationDraftCreated, session.AgentId, now);
                }

                await HandOffAsync(
                    turn, new AgentHandoffCause(HandoffCauseKind.AgentRequested, handedOff.Reason, version.Provider), AgentSessionStatus.HandedOff,
                    sendMessage: true, cancellationToken, handedOff.MemberId, handedOff.QuotationNumber);
                await CloseAsync(job, ModelJobStatus.Done, "handed_off", null);
                break;
            case AgentTurnRetry exhausted:
                await HandOffAsync(turn, new AgentHandoffCause(HandoffCauseKind.ProviderFailed, exhausted.Code, version.Provider), AgentSessionStatus.Failed, sendMessage: true, cancellationToken);
                await CloseAsync(job, ModelJobStatus.Failed, "provider_failed", exhausted.Code);
                break;
            case AgentTurnFailed failed:
                await FailAsync(turn, failed, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unknown outcome {outcome.GetType().Name}.");
        }
    }

    /// <summary>§6.3 paso 4: envía cada parte (D-A16: <c>job:{id}:{n}</c>), anota los turnos y revisa D-A21.</summary>
    private async Task ReplyAsync(TurnCommit turn, AgentReplied replied, CancellationToken cancellationToken)
    {
        var (parts, truncated) = ReplySplitter.Split(replied.Text, turn.Conversation.TextMaxLength, MaxReplyParts);
        for (var index = 0; index < parts.Count; index++)
        {
            var result = await outbound.SendTextAsync(
                turn.Job.TenantId, turn.Session.ConversationId, turn.Session.AgentId, AiDeterministicIds.ReplyClientId(turn.Job.Id, index), parts[index],
                cancellationToken);
            if (result is { Status: AiSendStatus.Sent, MessageId: { } messageId })
            {
                sessions.AddTurn(turn.Session.AppendTurn(
                    AgentTurnRoleKind.Agent, messageId, turn.Job.Id, index == 0 ? replied.SourceIds : [], truncated && index == parts.Count - 1, turn.Now));
                continue;
            }

            await SendFailedAsync(turn, result, cancellationToken);
            return;
        }

        turn.Session.RecordReply(turn.Now);
        if (turn.Session.RepliesWithoutHuman >= turn.Config.Handoff.MaxRepliesWithoutHuman)
        {
            await HandOffAsync(
                turn, new AgentHandoffCause(HandoffCauseKind.AgentLimitReached, AgentHandoffCodes.MaxRepliesWithoutHuman, turn.Version.Provider),
                AgentSessionStatus.HandedOff, sendMessage: true, cancellationToken);
        }

        await CloseAsync(turn.Job, ModelJobStatus.Done, "replied", null);
    }

    /// <summary>Plan 2026-11-12, «Errores del proveedor»: la respuesta inválida cuenta para <c>maxConsecutiveFailures</c> (ya se reintentó una
    /// vez en el bucle); todo lo demás es traspaso inmediato, <c>ProviderFailed</c> si es de la cuenta y <c>AgentFailed</c> si no.</summary>
    private async Task FailAsync(TurnCommit turn, AgentTurnFailed failed, CancellationToken cancellationToken)
    {
        var code = failed.Code ?? AgentHandoffCodes.For(failed.Reason);
        if (failed.Reason == AgentFailureReason.InvalidResponse
            && turn.Session.RecordFailure(turn.Now) < turn.Config.Handoff.MaxConsecutiveFailures)
        {
            await CloseAsync(turn.Job, ModelJobStatus.Failed, "agent_failed", code);
            return;
        }

        var kind = failed.Kind == AgentFailureKind.ProviderFailed ? HandoffCauseKind.ProviderFailed : HandoffCauseKind.AgentFailed;
        await HandOffAsync(turn, new AgentHandoffCause(kind, code, turn.Version.Provider), AgentSessionStatus.Failed, sendMessage: true, cancellationToken);
        await CloseAsync(turn.Job, ModelJobStatus.Failed, failed.Kind == AgentFailureKind.ProviderFailed ? "provider_failed" : "agent_failed", code);
    }

    /// <summary>Mensajería no pudo enviar. Un contacto bloqueado o una conversación que ya no es del agente terminan en silencio (como los
    /// chatbots); la ventana cerrada es D-A18; lo demás (conexión caída, mensaje rechazado) es traspaso para que una persona lo vea.</summary>
    private async Task SendFailedAsync(TurnCommit turn, AiSendResult result, CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case AiSendStatus.Blocked:
                if (turn.Session.IsBlock)
                {
                    // Spec 2026-11-17 §6.4: vuelve sin salida y el motor termina la corrida Interrupted; la sesión, Interrupted como hoy.
                    await AgentBlockReturn.EndAsync(
                        conversations, turn.Job.TenantId, turn.Session, new AgentBlockResult(null, AgentBlockReasons.Blocked), turn.Now, cancellationToken);
                }
                else
                {
                    turn.Session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.Blocked, null, turn.Now);
                }

                await CloseAsync(turn.Job, ModelJobStatus.Discarded, AgentSessionEndReasons.Blocked, null);
                return;
            case AiSendStatus.NotAutomated:
                turn.Session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, turn.Now);
                await CloseAsync(turn.Job, ModelJobStatus.Discarded, AgentSessionEndReasons.HumanTookOver, null);
                return;
            case AiSendStatus.WindowClosed:
                await HandOffAsync(
                    turn, new AgentHandoffCause(HandoffCauseKind.WindowClosed, null, turn.Version.Provider), AgentSessionStatus.WindowClosed,
                    sendMessage: false, cancellationToken);
                await CloseAsync(turn.Job, ModelJobStatus.Done, AgentSessionEndReasons.WindowClosed, null);
                return;
            default:
                await HandOffAsync(
                    turn,
                    new AgentHandoffCause(
                        HandoffCauseKind.AgentFailed, AgentHandoffCodes.SendFailed, turn.Version.Provider, result.ConnectionName, result.Code),
                    AgentSessionStatus.Failed, sendMessage: false, cancellationToken);
                await CloseAsync(turn.Job, ModelJobStatus.Failed, AgentHandoffCodes.SendFailed, result.Code);
                return;
        }
    }

    /// <summary>§6.6: el mensaje de traspaso al cliente (si está configurado y se puede enviar), el traspaso con su causa al destino
    /// configurado (o a <paramref name="memberId"/>, P-4b), y el fin de la sesión. Si una persona ya la tomó, el traspaso no hace falta.</summary>
    private async Task HandOffAsync(
        TurnCommit turn, AgentHandoffCause cause, AgentSessionStatus status, bool sendMessage, CancellationToken cancellationToken,
        Guid? memberId = null, string? quotationNumber = null)
    {
        // Spec 2026-11-17 §6.3 y D-B9: en un bloque nada traspasa ni manda el mensaje de traspaso; la conversación vuelve al flujo por la
        // salida de la causa (AgentBlockReturn.For) y el flujo decide a quién y con qué mensaje.
        if (turn.Session.IsBlock)
        {
            await AgentBlockReturn.EndAsync(
                conversations, turn.Job.TenantId, turn.Session, AgentBlockReturn.For(cause, memberId, quotationNumber), turn.Now, cancellationToken);
            return;
        }

        var handoff = turn.Config.Handoff;
        if (sendMessage && !string.IsNullOrWhiteSpace(handoff.Message))
        {
            // Un mensaje que no sale no frena el traspaso: la persona lo ve en la bandeja igual.
            await outbound.SendTextAsync(
                turn.Job.TenantId, turn.Session.ConversationId, turn.Session.AgentId, AiDeterministicIds.HandoffClientId(turn.Job.Id), handoff.Message.Trim(),
                cancellationToken);
        }

        try
        {
            await conversations.HandoffAsync(
                turn.Job.TenantId, turn.Session.ConversationId, cause, memberId ?? DestinationOf(handoff), cancellationToken);
        }
        catch (DomainException)
        {
            // Una persona la tomó entre la verificación y el traspaso: ya tiene humano.
        }

        turn.Session.Finish(status, cause.Kind.ToString(), cause.Code, turn.Now);
    }

    private async Task CloseAsync(ModelJob job, ModelJobStatus status, string outcome, string? errorCode)
    {
        job.Complete(status, outcome, errorCode, clock.UtcNow);
        // Sin cancelación: lo que se decidió (un envío, un traspaso) ya ocurrió en Mensajería.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        LogClosed(logger, job.TenantId, job.Id, outcome);
    }

    private static AgentTurnRole RoleOf(AgentTurnRoleKind role) => role switch
    {
        AgentTurnRoleKind.Customer => AgentTurnRole.Customer,
        AgentTurnRoleKind.Greeting => AgentTurnRole.Greeting,
        _ => AgentTurnRole.Agent,
    };

    private sealed record TurnCommit(
        ModelJob Job, AgentSession Session, AiConversationState Conversation, AgentVersion Version, AgentConfig Config, DateTimeOffset Now);

    /// <summary>La forma guardada de una respuesta ya calculada (§6.3 paso 4); sólo la lee este procesador.</summary>
    private sealed record StoredOutcome(
        string Type, string? Text, IReadOnlyList<string>? Sources, string? Reason, string? Note, AgentFailureKind? Kind, AgentFailureReason? FailureReason,
        string? Code, double? RetryAfterSeconds, Guid? MemberId = null, string? QuotationNumber = null, bool Wrote = false)
    {
        public static string Write(AgentTurnOutcome outcome) => JsonSerializer.Serialize(
            outcome switch
            {
                AgentReplied replied => new StoredOutcome("replied", replied.Text, replied.SourceIds, null, null, null, null, null, null, Wrote: replied.Wrote),
                // Spec 2026-11-17 D-B8: el resumen de finish_objective va en text.
                AgentFinished finished => new StoredOutcome("finished", finished.Summary, null, null, null, null, null, null, null),
                AgentHandedOff handedOff => new StoredOutcome(
                    "handed_off", null, null, handedOff.Reason, handedOff.Note, null, null, null, null, handedOff.MemberId, handedOff.QuotationNumber, handedOff.Wrote),
                AgentTurnRetry retry => new StoredOutcome("retry", null, null, null, null, null, null, retry.Code, retry.RetryAfter?.TotalSeconds),
                AgentTurnFailed failed => new StoredOutcome("failed", null, null, null, null, failed.Kind, failed.Reason, failed.Code, null, Wrote: failed.Wrote),
                _ => throw new InvalidOperationException($"Unknown outcome {outcome.GetType().Name}."),
            },
            Json);

        public static AgentTurnOutcome Read(string json)
        {
            var stored = JsonSerializer.Deserialize<StoredOutcome>(json, Json) ?? throw new InvalidOperationException("Empty stored outcome.");
            return stored.Type switch
            {
                "replied" => new AgentReplied(stored.Text ?? string.Empty, stored.Sources ?? [], stored.Wrote),
                "finished" => new AgentFinished(stored.Text ?? string.Empty),
                "handed_off" => new AgentHandedOff(stored.Reason ?? AgentHandoffReasons.All[0], stored.Note, stored.MemberId, stored.QuotationNumber, stored.Wrote),
                "retry" => new AgentTurnRetry(stored.Code ?? AiModelErrorCodes.ProviderUnavailable,
                    stored.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
                _ => new AgentTurnFailed(stored.Kind ?? AgentFailureKind.AgentFailed, stored.FailureReason ?? AgentFailureReason.ProviderError, stored.Code, stored.Wrote),
            };
        }
    }
}

/// <summary>§6.3: parte una respuesta larga por párrafos en hasta <c>maxParts</c> mensajes que caben en el canal; lo que sobra se corta
/// y la respuesta queda marcada como truncada.</summary>
public static class ReplySplitter
{
    public static (IReadOnlyList<string> Parts, bool Truncated) Split(string text, int maxLength, int maxParts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParts, 1);
        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return ([trimmed], false);
        }

        var parts = new List<string>();
        var current = string.Empty;
        foreach (var paragraph in trimmed.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pending = paragraph;
            while (pending.Length > 0)
            {
                var candidate = current.Length == 0 ? pending : $"{current}\n\n{pending}";
                if (candidate.Length <= maxLength)
                {
                    current = candidate;
                    pending = string.Empty;
                    continue;
                }

                if (current.Length > 0)
                {
                    parts.Add(current);
                    current = string.Empty;
                }
                else
                {
                    // Un párrafo solo que no cabe se corta en el tope.
                    parts.Add(pending[..maxLength]);
                    pending = pending[maxLength..].TrimStart();
                }

                if (parts.Count == maxParts)
                {
                    return (parts, true);
                }
            }
        }

        if (current.Length > 0)
        {
            if (parts.Count == maxParts)
            {
                return (parts, true);
            }

            parts.Add(current);
        }

        return (parts, false);
    }
}
