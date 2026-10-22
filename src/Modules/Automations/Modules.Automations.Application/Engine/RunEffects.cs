using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>Una corrida dentro de una unidad del procesador. <see cref="BaseStepNumber"/> es el <c>StepNumber</c> guardado al empezar la
/// unidad (0 en una corrida nueva): la base de los clientIds de P2-R5.</summary>
public sealed record RunUnit(ConversationRun Run, FlowDefinition Flow, EngineSettings Settings, int BaseStepNumber);

/// <summary>
/// Spec 2026-10-21 §5.2 paso 5, D-M5 y D-M14: el bucle de efectos que <see cref="RunProcessor"/> delega. Ejecuta en orden lo que pide
/// <see cref="FlowEngine.Step"/>, vuelve a entrar al motor con el resultado de un horario o de un HTTP, y deja el desenlace en la corrida
/// (que el procesador guarda). Traspasar y resolver son siempre el último efecto de un paso, así que van justo antes del guardado de
/// Automations (P2-R23): si ese guardado fallara después, el reintento encuentra la conversación pausada o resuelta con la corrida viva y
/// la cierra <c>Interrupted</c> (D-M15). Es raro y el lado de Mensajería ya quedó bien; se acepta.
/// </summary>
public sealed partial class RunEffects(
    IAutomationMessenger messenger,
    IAutomationConversations conversations,
    IAutomationBusinessHours businessHours,
    IAutomationsAuditRecorder audit,
    ILogger<RunEffects> logger)
{
    /// <summary>Lo que devuelve un bloque HTTP hasta que el ejecutor exista (tarea 12): sale por <c>error</c>.</summary>
    public const string HttpNotAvailable = "http.not_available";

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {ConversationId} of tenant {TenantId} left the bot during run {RunId}; the run ends Interrupted.")]
    private static partial void LogTakenOver(ILogger logger, Guid tenantId, Guid conversationId, Guid runId);

    /// <summary>Entra al motor con <paramref name="input"/> y ejecuta lo que pida hasta que la corrida espere, termine o se corte.</summary>
    public async Task RunAsync(RunUnit unit, EngineInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var run = unit.Run;
        var state = run.ToState();
        // P2-R5: el orden de cada envío dentro de la unidad, también los que siguen a un HTTP o a un horario. Riesgo aceptado (raro): si
        // el reintento de una unidad deshecha toma otra rama (el horario cambió de abierto a cerrado entre los dos intentos), su envío
        // número N reusa el clientId del N del primer intento; si ése había salido, Mensajería lo da por enviado y el mensaje nuevo no sale.
        var sends = 0;
        EngineInput? next = input;
        while (next is not null)
        {
            var step = FlowEngine.Step(unit.Flow, unit.Settings, state, next, now);
            state = step.State;
            next = null;
            foreach (var effect in step.Effects)
            {
                switch (effect)
                {
                    case Effect.SendText or Effect.SendInteractive:
                        var clientId = DeterministicClientId.For(run.Id, unit.BaseStepNumber, sends++);
                        if (await SendAsync(run, effect, clientId, cancellationToken) is not BotSendOutcome.Sent and var refused)
                        {
                            await StopAsync(run, state, refused, now, cancellationToken);
                            return;
                        }

                        break;
                    case Effect.WaitForCustomer wait:
                        await conversations.SetStepAsync(run.TenantId, run.ConversationId, wait.StepTitle, cancellationToken);
                        break;
                    case Effect.CheckBusinessHours or Effect.CallHttp:
                        next = await ExternalAsync(run, effect, cancellationToken);
                        break;
                    case Effect.Handoff:
                        if (!await HandsOverAsync(run, state, () => conversations.HandoffAsync(run.TenantId, run.ConversationId, cancellationToken), now))
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
                        await FailAsync(run, state, AutomationsRunFailures.Of(end.Reason), now, cancellationToken);
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
            HttpNode http => await ExternalAsync(unit.Run, new Effect.CallHttp(http.Id, http), cancellationToken),
            BusinessHoursNode hours => await ExternalAsync(unit.Run, new Effect.CheckBusinessHours(hours.Id), cancellationToken),
            _ => null,
        };
    }

    /// <summary>D-M14: <c>Failed</c> pasa a <c>Paused</c> con <c>Handoff</c> para que la vea un asesor.</summary>
    public async Task FailAsync(ConversationRun run, RunState state, string failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!await HandsOverAsync(run, state, () => conversations.HandoffAsync(run.TenantId, run.ConversationId, cancellationToken), now))
        {
            return;
        }

        run.Close(state, RunStatus.Failed, failureCode, now);
        Audit(run, AutomationsAuditActions.RunFailed, now);
    }

    private Task<BotSendOutcome> SendAsync(ConversationRun run, Effect effect, Guid clientId, CancellationToken cancellationToken) => effect switch
    {
        Effect.SendText text => messenger.SendTextAsync(run.TenantId, run.ConversationId, run.ChatbotId, clientId, text.Text, cancellationToken),
        Effect.SendInteractive card => messenger.SendInteractiveAsync(run.TenantId, run.ConversationId, run.ChatbotId, clientId, card.Message, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No es un envío."),
    };

    /// <summary>§5.2 paso 5: un envío que Mensajería no hizo corta la unidad.</summary>
    private async Task StopAsync(ConversationRun run, RunState state, BotSendOutcome refused, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (refused)
        {
            case BotSendOutcome.NotAutomated:
                // D-M15: una persona la tomó entre el reclamo y el envío. No es un fallo: no se traspasa (ya tiene quien la atienda).
                LogTakenOver(logger, run.TenantId, run.ConversationId, run.Id);
                run.Close(state, RunStatus.Interrupted, null, now);
                break;
            case BotSendOutcome.WindowClosed:
                run.Close(state, RunStatus.WindowClosed, null, now);
                await conversations.EndAsync(run.TenantId, run.ConversationId, cancellationToken);
                break;
            case BotSendOutcome.Rejected rejected:
                // P2-R21: el código es el de Mensajería (messaging.message.rejected, messaging.connection_unavailable, …).
                await FailAsync(run, state, rejected.Code, now, cancellationToken);
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

    private async Task<EngineInput> ExternalAsync(ConversationRun run, Effect effect, CancellationToken cancellationToken) => effect switch
    {
        Effect.CheckBusinessHours hours => new EngineInput.BusinessHours(
            hours.NodeId, await businessHours.IsOpenAsync(run.TenantId, run.ConnectionId, cancellationToken)),
        // Tarea 12 trae el ejecutor del bloque; hasta entonces sale por «error» sin llamar a nadie.
        Effect.CallHttp http => new EngineInput.HttpResult(http.NodeId, Ok: false, Status: null, RunVariables.Empty(), HttpNotAvailable),
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No es un efecto externo."),
    };

    private void Audit(ConversationRun run, string action, DateTimeOffset now) =>
        audit.Record(run.TenantId, Guid.Empty, bySystem: true, action, AutomationsAuditActions.ResourceRun, run.Id, now);
}
