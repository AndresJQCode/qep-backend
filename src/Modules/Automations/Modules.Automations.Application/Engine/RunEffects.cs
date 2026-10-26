using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>Una corrida dentro de una unidad del procesador. <see cref="BaseStepNumber"/> es el <c>StepNumber</c> guardado al empezar la
/// unidad (0 en una corrida nueva): la base de los clientIds de P2-R5.</summary>
public sealed record RunUnit(ConversationRun Run, FlowDefinition Flow, EngineSettings Settings, int BaseStepNumber);

/// <summary>
/// Spec 2026-10-21 §5.2 paso 5, D-M5 y D-M14: el bucle de efectos que <see cref="RunProcessor"/> delega. Ejecuta en orden lo que pide
/// <see cref="FlowEngine.Step"/>, vuelve a entrar al motor con el resultado de un horario, de un HTTP (D-M2; el bloque HTTP lo ejecuta
/// <see cref="IHttpBlockExecutor"/>, que nunca lanza por la API) o de la verificación de Política de datos (spec 2026-10-26 D-2), y
/// deja el desenlace en la corrida (que el procesador guarda). Traspasar y resolver son siempre el último efecto de un paso, así que van justo antes del guardado de
/// Automations (P2-R23): si ese guardado fallara después, el reintento encuentra la conversación pausada o resuelta con la corrida viva y
/// la cierra <c>Interrupted</c> (D-M15). Es raro y el lado de Mensajería ya quedó bien; se acepta.
/// </summary>
public sealed partial class RunEffects(
    IAutomationMessenger messenger,
    IAutomationConversations conversations,
    IAutomationBusinessHours businessHours,
    IHttpBlockExecutor httpBlocks,
    IAutomationMedia media,
    IAutomationsAuditRecorder audit,
    IAutomationDataConsents dataConsents,
    IChatbotRepository chatbots,
    ILogger<RunEffects> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {ConversationId} of tenant {TenantId} left the bot during run {RunId}; the run ends Interrupted.")]
    private static partial void LogTakenOver(ILogger logger, Guid tenantId, Guid conversationId, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Contact of conversation {ConversationId} of tenant {TenantId} is blocked during run {RunId}; the run ends Interrupted.")]
    private static partial void LogContactBlocked(ILogger logger, Guid tenantId, Guid conversationId, Guid runId);

    /// <summary>Entra al motor con <paramref name="input"/> y ejecuta lo que pida hasta que la corrida espere, termine o se corte.</summary>
    public async Task RunAsync(RunUnit unit, EngineInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var run = unit.Run;
        var state = run.ToState();
        // P2-R43: las variables que escribe un Pedir adjunto, las únicas que pueden quedar con stored: false.
        string[] attachments = [.. unit.Flow.Nodes.OfType<AttachmentNode>().Select(node => node.SaveTo).Distinct(StringComparer.Ordinal)];
        // P2-R5: el orden de cada envío dentro de la unidad, también los que siguen a un HTTP o a un horario. Riesgo aceptado (raro): si
        // el reintento de una unidad deshecha toma otra rama (el horario cambió de abierto a cerrado entre los dos intentos), su envío
        // número N reusa el clientId del N del primer intento; si ése había salido, Mensajería lo da por enviado y el mensaje nuevo no sale.
        var sends = 0;
        var reentries = 0;
        EngineInput? next = input;
        while (next is not null)
        {
            // Spec 2026-10-26 D-7: sin cliente al que registrarle la respuesta, el motor sale por «rejected» sin preguntar; la auditoría es
            // la única huella. Acá y no en el efecto: la entrada también puede venir de PendingInputAsync.
            if (next is EngineInput.DataConsent { CanRecord: false })
            {
                Audit(run, AutomationsAuditActions.DataConsentUnavailable, now);
            }

            // P2-R43: el motor interpola los textos de SendText y SendInteractive dentro del paso, así que el refresco de los adjuntos va
            // antes de cada paso (el de la unidad y el de cada reentrada); un adjunto que se recibe en este mismo paso todavía no tiene url.
            state = await RefreshAttachmentsAsync(run, state, attachments, cancellationToken);
            // Spec 2026-10-24 D-T6: un recorrido sólo se corta al esperar o al pedir un efecto externo, así que todo traspaso de este paso
            // es consecuencia de la entrada que lo arrancó. Si fue un HTTP fallido, la causa lo nombra.
            var http = FailedHttpOf(unit.Flow, next);
            var step = FlowEngine.Step(unit.Flow, unit.Settings, state, next, now);
            state = step.State;
            next = null;
            foreach (var effect in step.Effects)
            {
                switch (effect)
                {
                    case Effect.SendText or Effect.SendInteractive:
                        var clientId = DeterministicClientId.For(run.Id, unit.BaseStepNumber, sends++);
                        var outcome = await SendAsync(run, effect, clientId, cancellationToken);
                        if (outcome is not BotSendOutcome.Sent sent)
                        {
                            await StopAsync(run, state, outcome, effect, TitleOf(unit.Flow, effect.NodeId), http, now, cancellationToken);
                            return;
                        }

                        // Spec 2026-10-26 D-6: la pregunta de Política de datos recuerda qué mensaje salió, para registrarlo con la respuesta.
                        if (effect is Effect.SendInteractive
                            && state.ConsentPrompt is { PromptMessageId: null } prompt
                            && prompt.NodeId == effect.NodeId)
                        {
                            state = state with { ConsentPrompt = prompt with { PromptMessageId = sent.MessageId } };
                        }

                        break;
                    case Effect.WaitForCustomer wait:
                        await conversations.SetStepAsync(run.TenantId, run.ConversationId, wait.StepTitle, cancellationToken);
                        break;
                    case Effect.CheckBusinessHours or Effect.CallHttp or Effect.CheckDataConsent:
                        // P2-R40: una salida que vuelve al bloque (o a uno anterior) daría vueltas dentro de la unidad; se trata como el ciclo
                        // de P2-R13. FailAsync traspasa al final, como cualquier traspaso (P2-R23).
                        if (++reentries > EngineReentries.Max)
                        {
                            await FailAsync(
                                run, state,
                                HandoffCause.FlowFailed(
                                    AutomationsRunFailures.Of(EndReasons.LoopDetected), effect.NodeId, TitleOf(unit.Flow, effect.NodeId), http),
                                now, cancellationToken);
                            return;
                        }

                        // El bloque lee las variables como quedaron en este paso (lo que se guardó antes de llegar a él), con los adjuntos
                        // que llegaron mientras tanto ya firmados (P2-R43; si el bloque usa la url de uno que no llegó, el ejecutor lo
                        // espera). P2-R5: el contador de envíos sigue después de la reentrada, no vuelve a cero.
                        if (effect is Effect.CallHttp)
                        {
                            state = await RefreshAttachmentsAsync(run, state, attachments, cancellationToken);
                        }

                        next = await ExternalAsync(run, effect, state.Variables, cancellationToken);
                        break;
                    case Effect.RecordDataConsent record:
                        // D-5: la respuesta se registra antes de seguir con el bloque siguiente; sin registro, la corrida no sigue.
                        if (!await RecordDataConsentAsync(unit, state, record, http, now, cancellationToken))
                        {
                            return;
                        }

                        break;
                    case Effect.DataConsentUnanswered:
                        // D-4: agotar los intentos no registra nada; queda la auditoría.
                        Audit(run, AutomationsAuditActions.DataConsentUnanswered, now);
                        break;
                    case Effect.Handoff handoff:
                        // D-T9: el motor dice por qué camino llegó; el procesador no lo deduce por el tipo de bloque.
                        var cause = new HandoffCause(
                            handoff.Reason switch
                            {
                                HandoffReasons.InvalidReply => HandoffCauseKind.InvalidReply,
                                HandoffReasons.AttemptsExhausted => HandoffCauseKind.AttemptsExhausted,
                                _ => HandoffCauseKind.Block,
                            },
                            null, handoff.NodeId, TitleOf(unit.Flow, handoff.NodeId), null, null, http);
                        // Spec 2026-10-25 D-1: el motor trae el destino; el adaptador decide si todavía puede responder (D-5).
                        if (!await HandsOverAsync(run, state, () => conversations.HandoffAsync(run.TenantId, run.ConversationId, cause, handoff.MemberId, cancellationToken), now))
                        {
                            return;
                        }

                        Audit(run, AutomationsAuditActions.RunHandedOff, now);
                        break;
                    case Effect.Resolve resolve:
                        if (!await HandsOverAsync(run, state, () => conversations.ResolveAsync(run.TenantId, run.ConversationId, resolve.ReasonId, cancellationToken), now))
                        {
                            return;
                        }

                        Audit(run, AutomationsAuditActions.RunResolved, now);
                        break;
                    case Effect.EndRun end when end.Reason == EndReasons.OutputUnconnected:
                        // D-M14: un bot que terminó no sigue «atendiendo» en la bandeja.
                        await conversations.EndAsync(run.TenantId, run.ConversationId, cancellationToken);
                        Audit(run, AutomationsAuditActions.RunCompleted, now);
                        break;
                    case Effect.EndRun end:
                        // P2-R13/R14/R17: un ciclo o un bloque que no existe; que lo vea una persona.
                        await FailAsync(
                            run, state, HandoffCause.FlowFailed(AutomationsRunFailures.Of(end.Reason), end.NodeId, TitleOf(unit.Flow, end.NodeId), http),
                            now, cancellationToken);
                        return;
                    default:
                        throw new InvalidOperationException($"Efecto del motor desconocido: {effect.GetType().Name}.");
                }
            }
        }

        run.Apply(state, now);
    }

    /// <summary>
    /// Una corrida <c>Running</c> guardada: una unidad anterior murió a mitad de un efecto externo. Se vuelve a pedir el del bloque en que
    /// quedó; <c>null</c> si ese bloque no pide ninguno (sólo con una versión corrupta).
    /// </summary>
    public async Task<EngineInput?> PendingInputAsync(RunUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Run.CurrentNodeId is null)
        {
            return new EngineInput.Start();
        }

        return unit.Flow.Nodes.FirstOrDefault(node => node.Id == unit.Run.CurrentNodeId) switch
        {
            HttpNode http => await ExternalAsync(unit.Run, new Effect.CallHttp(http.Id, http), unit.Run.ToState().Variables, cancellationToken),
            BusinessHoursNode hours => await ExternalAsync(unit.Run, new Effect.CheckBusinessHours(hours.Id), unit.Run.ToState().Variables, cancellationToken),
            DataConsentNode consent => await ExternalAsync(unit.Run, new Effect.CheckDataConsent(consent.Id), unit.Run.ToState().Variables, cancellationToken),
            _ => null,
        };
    }

    /// <summary>D-M14: <c>Failed</c> pasa a <c>Paused</c> con <c>Handoff</c> para que la vea un asesor. Spec 2026-10-24: la corrida cierra con
    /// <c>cause.Code</c> como <c>failure_code</c>, así la causa del hilo y el código guardado no pueden divergir. D-T2: no le escribe al
    /// cliente; sólo pausa, cierra y audita.</summary>
    public async Task FailAsync(ConversationRun run, RunState state, HandoffCause cause, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cause);
        if (cause.Code is not { } failureCode)
        {
            throw new ArgumentException("A failed run needs its failure code.", nameof(cause));
        }

        // Spec 2026-10-25 D-1: un fallo nunca asigna; que lo vea quien mire la bandeja.
        if (!await HandsOverAsync(run, state, () => conversations.HandoffAsync(run.TenantId, run.ConversationId, cause, null, cancellationToken), now))
        {
            return;
        }

        run.Close(state, RunStatus.Failed, failureCode, now);
        Audit(run, AutomationsAuditActions.RunFailed, now);
    }

    /// <summary>Spec 2026-10-24 §4.1: el título del primer bloque con ese id, como lo busca el motor; <c>null</c> si no está (el Inicio, una
    /// versión corrupta).</summary>
    public static string? TitleOf(FlowDefinition flow, string? nodeId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return nodeId is null ? null : flow.Nodes.FirstOrDefault(node => node.Id == nodeId)?.Title;
    }

    private static HandoffHttpFailure? FailedHttpOf(FlowDefinition flow, EngineInput input) =>
        input is EngineInput.HttpResult { Ok: false } failed
            ? new HandoffHttpFailure(
                failed.NodeId, TitleOf(flow, failed.NodeId), failed.Outcome ?? FlowOutputs.Error, failed.ErrorCode, failed.Status, failed.ConnectionName)
            : null;

    private async Task<RunState> RefreshAttachmentsAsync(ConversationRun run, RunState state, string[] attachments, CancellationToken cancellationToken)
    {
        if (attachments.Length == 0)
        {
            return state;
        }

        var variables = await AttachmentUrls.RefreshAsync(media, run.TenantId, state.Variables, attachments, cancellationToken);
        return ReferenceEquals(variables, state.Variables) ? state : state with { Variables = variables };
    }

    private Task<BotSendOutcome> SendAsync(ConversationRun run, Effect effect, Guid clientId, CancellationToken cancellationToken) => effect switch
    {
        Effect.SendText text => messenger.SendTextAsync(run.TenantId, run.ConversationId, run.ChatbotId, clientId, text.Text, cancellationToken),
        Effect.SendInteractive card => messenger.SendInteractiveAsync(run.TenantId, run.ConversationId, run.ChatbotId, clientId, card.Message, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No es un envío."),
    };

    /// <summary>§5.2 paso 5: un envío que Mensajería no hizo corta la unidad. Spec 2026-10-24 §4.1 fila 3: un rechazo traspasa con el bloque
    /// cuyo mensaje no salió, la conexión de WhatsApp y su motivo, y el HTTP fallido que arrancó el paso.</summary>
    private async Task StopAsync(
        ConversationRun run, RunState state, BotSendOutcome refused, Effect effect, string? nodeTitle, HandoffHttpFailure? http, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (refused)
        {
            case BotSendOutcome.NotAutomated:
                // D-M15: una persona la tomó entre el reclamo y el envío. No es un fallo: no se traspasa (ya tiene quien la atienda).
                LogTakenOver(logger, run.TenantId, run.ConversationId, run.Id);
                run.Close(state, RunStatus.Interrupted, null, now);
                break;
            case BotSendOutcome.Blocked:
                // Spec 2026-10-26 §3.4: bloquear al contacto no es un fallo ni pide traspaso, y no toca la automatización: sin FailAsync,
                // HandoffAsync ni EndAsync. Al desbloquear, la conversación sigue donde estaba.
                LogContactBlocked(logger, run.TenantId, run.ConversationId, run.Id);
                run.Close(state, RunStatus.Interrupted, null, now);
                break;
            case BotSendOutcome.WindowClosed:
                run.Close(state, RunStatus.WindowClosed, null, now);
                await conversations.EndAsync(run.TenantId, run.ConversationId, cancellationToken);
                break;
            case BotSendOutcome.Rejected rejected:
                // P2-R21: el código es el de Mensajería (messaging.message.rejected, messaging.connection_unavailable, …).
                await FailAsync(
                    run, state,
                    new HandoffCause(
                        HandoffCauseKind.SendFailed, rejected.Code, effect.NodeId, nodeTitle, rejected.ConnectionName, rejected.ConnectionReason, http),
                    now, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(refused), refused.GetType().Name, "Resultado de envío desconocido.");
        }
    }

    /// <summary>
    /// Traspasar o resolver. Mensajería rechaza con su <see cref="DomainException"/> una conversación que una persona ya pausó, tomó o
    /// resolvió en el medio: la corrida termina <c>Interrupted</c> en vez de deshacer la unidad (y reintentar algo que ya no le toca al
    /// bot). Un <c>concurrency.conflict</c> sí sube: la fila se reprograma y el reintento repite lo mismo.
    /// </summary>
    private async Task<bool> HandsOverAsync(ConversationRun run, RunState state, Func<Task> change, DateTimeOffset now)
    {
        try
        {
            await change();
            return true;
        }
        catch (DomainException)
        {
            LogTakenOver(logger, run.TenantId, run.ConversationId, run.Id);
            run.Close(state, RunStatus.Interrupted, null, now);
            return false;
        }
    }

    private async Task<EngineInput> ExternalAsync(ConversationRun run, Effect effect, RunVariables variables, CancellationToken cancellationToken) =>
        effect switch
        {
            Effect.CheckBusinessHours hours => new EngineInput.BusinessHours(
                hours.NodeId, await businessHours.IsOpenAsync(run.TenantId, run.ConnectionId, cancellationToken)),
            Effect.CallHttp http => await CallHttpAsync(run, http, variables, cancellationToken),
            Effect.CheckDataConsent consent => await CheckDataConsentAsync(run, consent, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No es un efecto externo."),
        };

    /// <summary>
    /// D-H4..D-H8. El ejecutor resuelve la conexión, llama, registra su fila de <c>http_calls</c> por su cuenta (P2-R33: confirmada aunque
    /// esta unidad se deshaga) y devuelve cualquier fallo de la API como resultado, nunca como excepción (P2-R33a): un throw desharía la
    /// unidad y el reintento de la fila volvería a llamar a la API. El motor sólo mezcla <see cref="HttpCallResult.Mapped"/> si salió bien.
    /// </summary>
    private async Task<EngineInput> CallHttpAsync(ConversationRun run, Effect.CallHttp http, RunVariables variables, CancellationToken cancellationToken)
    {
        var result = await httpBlocks.ExecuteAsync(new HttpCallContext(run.TenantId, run.Id, null, http.NodeId), http.Node, variables, cancellationToken);
        // Spec 2026-10-24 §4.2: el outcome y el nombre de la conexión no los lee el motor; viajan para explicar un traspaso.
        return new EngineInput.HttpResult(http.NodeId, result.Ok, result.Status, result.Mapped, result.ErrorCode, result.Outcome, result.ConnectionName);
    }

    /// <summary>Spec 2026-10-26 D-2 y D-7: <see cref="DataConsentCheck.Unavailable"/> no es una aceptación y no deja registrar.</summary>
    private async Task<EngineInput> CheckDataConsentAsync(ConversationRun run, Effect.CheckDataConsent consent, CancellationToken cancellationToken)
    {
        var check = await dataConsents.CheckAsync(run.TenantId, run.ConversationId, cancellationToken);
        return new EngineInput.DataConsent(consent.NodeId, check == DataConsentCheck.Accepted, CanRecord: check != DataConsentCheck.Unavailable);
    }

    /// <summary>
    /// Spec 2026-10-26 D-5 y D-10: registra la respuesta con la versión de la corrida y el entrante que la respondió. <c>false</c> si la
    /// corrida falló (la versión ya no existe, o Customers no pudo registrarla); quien llama corta la unidad. Lo que lance el puerto (un
    /// conflicto de concurrencia) sube como en cualquier otro efecto: la unidad se deshace y la fila se reintenta.
    /// </summary>
    private async Task<bool> RecordDataConsentAsync(
        RunUnit unit, RunState state, Effect.RecordDataConsent record, HandoffHttpFailure? http, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var run = unit.Run;
        var title = TitleOf(unit.Flow, record.NodeId);
        if (await chatbots.FindVersionLabelAsync(run.TenantId, run.ChatbotVersionId, cancellationToken) is not { } label)
        {
            await FailAsync(run, state, HandoffCause.FlowFailed(AutomationsRunFailures.VersionMissing, record.NodeId, title, http), now, cancellationToken);
            return false;
        }

        // El procesador marca el entrante antes de entrar al motor; una respuesta sin él no puede pasar.
        var replyMessageId = run.LastInboundMessageId
            ?? throw new InvalidOperationException($"La corrida {run.Id} respondió Política de datos sin un entrante marcado.");
        var recorded = await dataConsents.RecordAsync(
            new DataConsentRecord(
                run.TenantId, run.ConversationId, record.Decision, record.ShownText, record.PromptMessageId, replyMessageId, run.ChatbotId,
                label.ChatbotName, run.ChatbotVersionId, label.Number, record.NodeId),
            cancellationToken);
        if (!recorded)
        {
            await FailAsync(
                run, state, HandoffCause.FlowFailed(AutomationsRunFailures.DataConsentUnrecorded, record.NodeId, title, http), now, cancellationToken);
            return false;
        }

        return true;
    }

    private void Audit(ConversationRun run, string action, DateTimeOffset now) =>
        audit.Record(run.TenantId, Guid.Empty, bySystem: true, action, AutomationsAuditActions.ResourceRun, run.Id, now);
}
