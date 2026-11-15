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
    ILogger<AgentSessionJobProcessor> logger)
{
    /// <summary>§6.3 paso 4: el candado estaba tomado; la respuesta guardada se reintenta en 2 s sin llamar al modelo otra vez.</summary>
    internal static readonly TimeSpan LockedRetryDelay = TimeSpan.FromSeconds(2);

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
        var agent = session is null ? null : await sessions.FindAgentAsync(job.TenantId, session.AgentId, cancellationToken);
        var version = agent?.Versions.FirstOrDefault(candidate => candidate.Id == session!.AgentVersionId);
        if (session is null || !session.IsLive || version is null)
        {
            await CloseAsync(job, ModelJobStatus.Discarded, "session_ended", null);
            return;
        }

        var config = AgentConfig.FromJson(version.ConfigJson);
        var outcome = job.ResultJson is { } held ? StoredOutcome.Read(held) : await RunAsync(job, session, version, config, cancellationToken);
        if (outcome is null)
        {
            // Reprogramado por un error transitorio (D-A17).
            return;
        }

        await CommitAsync(job, session.ConversationId, version, config, outcome, cancellationToken);
    }

    /// <summary>La etapa larga: sin transacción ni candado. <c>null</c> si el trabajo quedó reprogramado.</summary>
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
        var turns = await sessions.ListTurnsAsync(session.Id, job.UpToSeq, cancellationToken);
        var messages = await conversations.ReadMessagesAsync(
            job.TenantId, [.. turns.Select(turn => turn.MessageId).OfType<Guid>()], cancellationToken);
        var history = turns
            .Where(turn => turn.MessageId is { } id && messages.ContainsKey(id))
            .Select(turn => new AgentHistoryTurn(RoleOf(turn.Role), messages[turn.MessageId!.Value].Text, messages[turn.MessageId!.Value].Kind))
            .ToList();

        var replies = config.UseQuickReplies ? await quickReplies.ListAsync(job.TenantId, cancellationToken) : [];
        var prompt = promptBuilder.Build(new AgentPromptInput(
            config, replies, history, await tenantClock.GetAsync(job.TenantId, cancellationToken), conversation?.WindowRemaining));
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
            IsTest: false);

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
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.ModuleDisabled, null, now);
            await AiHandoffs.ModuleDisabledAsync(conversations, sessions, job.TenantId, conversation, session, cancellationToken);
            await CloseAsync(job, ModelJobStatus.Discarded, AgentSessionEndReasons.ModuleDisabled, null);
            return;
        }

        if (conversation is null || AgentAttendance.HumanTookOver(conversation, session))
        {
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, now);
            await CloseAsync(job, ModelJobStatus.Discarded, AgentSessionEndReasons.HumanTookOver, null);
            return;
        }

        if (session.CanSupersede && await sessions.HasCustomerTurnAfterAsync(session.Id, job.UpToSeq, cancellationToken))
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
            case AgentHandedOff handedOff:
                await HandOffAsync(turn, new AgentHandoffCause(HandoffCauseKind.AgentRequested, handedOff.Reason, version.Provider), AgentSessionStatus.HandedOff, sendMessage: true, cancellationToken);
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
                turn.Session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.Blocked, null, turn.Now);
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
    /// configurado, y el fin de la sesión. Si una persona ya la tomó, el traspaso no hace falta.</summary>
    private async Task HandOffAsync(
        TurnCommit turn, AgentHandoffCause cause, AgentSessionStatus status, bool sendMessage, CancellationToken cancellationToken)
    {
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
                turn.Job.TenantId, turn.Session.ConversationId, cause,
                handoff.Destination == AgentHandoff.ToMember ? handoff.MemberId : null, cancellationToken);
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
        string? Code, double? RetryAfterSeconds)
    {
        public static string Write(AgentTurnOutcome outcome) => JsonSerializer.Serialize(
            outcome switch
            {
                AgentReplied replied => new StoredOutcome("replied", replied.Text, replied.SourceIds, null, null, null, null, null, null),
                AgentHandedOff handedOff => new StoredOutcome("handed_off", null, null, handedOff.Reason, handedOff.Note, null, null, null, null),
                AgentTurnRetry retry => new StoredOutcome("retry", null, null, null, null, null, null, retry.Code, retry.RetryAfter?.TotalSeconds),
                AgentTurnFailed failed => new StoredOutcome("failed", null, null, null, null, failed.Kind, failed.Reason, failed.Code, null),
                _ => throw new InvalidOperationException($"Unknown outcome {outcome.GetType().Name}."),
            },
            Json);

        public static AgentTurnOutcome Read(string json)
        {
            var stored = JsonSerializer.Deserialize<StoredOutcome>(json, Json) ?? throw new InvalidOperationException("Empty stored outcome.");
            return stored.Type switch
            {
                "replied" => new AgentReplied(stored.Text ?? string.Empty, stored.Sources ?? []),
                "handed_off" => new AgentHandedOff(stored.Reason ?? AgentHandoffReasons.All[0], stored.Note),
                "retry" => new AgentTurnRetry(stored.Code ?? AiModelErrorCodes.ProviderUnavailable,
                    stored.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
                _ => new AgentTurnFailed(stored.Kind ?? AgentFailureKind.AgentFailed, stored.FailureReason ?? AgentFailureReason.ProviderError, stored.Code),
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
