using BuildingBlocks.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>Spec 2026-10-31: por qué una persona puede o no iniciar el asistente desde el hilo. El orden es el de la decisión.</summary>
public enum ManualStartVerdict
{
    Available,
    NotFound,
    NotOpen,
    HasAdvisor,
    Blocked,
    UnsupportedChannel,
    ModuleDisabled,
    BotAttending,
    StartPending,
    NoChatbot,
    WindowClosed,
    Busy,

    /// <summary>Spec 2026-11-01: sólo al continuar. El asistente se podría iniciar, pero no hay paso pendiente que retomar.</summary>
    CannotContinue,
}

/// <summary>Spec 2026-11-01 §5: qué hace la primera opción del modal.</summary>
public enum ManualContinueMode
{
    /// <summary>Hay paso pendiente: «Continuar donde iba».</summary>
    PendingStep,

    /// <summary>Sin paso que retomar (D-7): el flujo empieza cuando el cliente escriba.</summary>
    FlowStart,
}

/// <summary>Spec 2026-11-01 D-6: lo que el resumen le dice al modal. <paramref name="CustomerWroteDuringPause"/> (D-10) avisa que hay
/// entrantes que el asistente no va a leer.</summary>
public sealed record ManualContinueOffer(ManualContinueMode Mode, bool CustomerWroteDuringPause);

/// <summary>Una sola evaluación para los dos campos del resumen: <paramref name="Continue"/> es <c>null</c> si no hay modal de dos
/// opciones.</summary>
public sealed record ManualStartEvaluation(ManualStartVerdict Verdict, ManualContinueOffer? Continue);

/// <summary>
/// Spec 2026-10-31 D-6 y D-7: decide y resguarda el arranque manual del asistente. <see cref="EvaluateAsync"/> es una lectura sin
/// transacción (el resumen de la conversación la llama en cada detalle); <see cref="StartAsync"/> repite la misma decisión bajo el
/// candado de la conversación, cierra la corrida muerta y deja que Mensajería confirme el cambio. No es un mismo guardado: son dos
/// <c>DbContext</c> y Mensajería confirma primero; si el guardado de Automations falla después, la corrida muerta queda abierta y el
/// worker la cierra por <c>AutomationResumedAt</c> (<see cref="BotAttendance.HumanTookOver"/>). <see cref="ContinueAsync"/> (spec
/// 2026-11-01) hace lo mismo para retomar el paso pendiente de una pausa manual, sin cerrar ni arrancar ninguna corrida.
/// </summary>
public sealed class ManualRunStarter(
    IAutomationsUnitOfWork unitOfWork,
    IConversationRunRepository runs,
    IChatbotRepository chatbots,
    IAutomationInboundQueue inbox,
    IAutomationConversations conversations,
    IAutomationsModuleStatus modules,
    RunUnitWriter recorder,
    IClock clock)
{
    /// <summary>Sin transacción ni candado: a lo sumo hay una corrida viva y es la última (D-6).</summary>
    public async Task<ManualStartEvaluation> EvaluateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        // La corrida se lee sólo si la decisión llega a necesitarla (BotAttending); los veredictos de la conversación no la leen. Se guarda
        // porque continuar pregunta por la misma: con el botón disponible ya se leyó, y el resumen no paga una segunda lectura.
        ConversationRun? live = null;
        async Task<ConversationRun?> LoadLiveAsync(CancellationToken token)
        {
            var latest = await runs.FindLatestAsync(tenantId, conversationId, token);
            live = latest is { IsLive: true } ? latest : null;
            return live;
        }

        var (verdict, conversation, chatbot) = await DecideAsync(tenantId, conversationId, LoadLiveAsync, cancellationToken);
        if (verdict != ManualStartVerdict.Available || conversation is not { AutomationStatus: AutomationStates.Paused })
        {
            return new ManualStartEvaluation(verdict, null);
        }

        var mode = await CanContinueAsync(tenantId, conversationId, conversation, live, chatbot!, cancellationToken)
            ? ManualContinueMode.PendingStep
            : ManualContinueMode.FlowStart;
        return new ManualStartEvaluation(verdict, new ManualContinueOffer(mode, conversation.LastInboundAt > conversation.AutomationPausedAt));
    }

    /// <summary>Spec 2026-11-01 D-8: como <see cref="StartAsync"/>, repite la decisión bajo el candado y sólo entonces deja que Mensajería
    /// confirme. El barrido no sube la versión de la conversación, así que una corrida cerrada con el modal abierto sólo se ve aquí. Son
    /// dos <c>DbContext</c> y Mensajería confirma primero; si el guardado de Automations falla, la corrida sigue viva y el continuar
    /// funciona: sólo queda sin renovar su reloj del barrido (D-9), y eso se acepta.</summary>
    /// <param name="commit">Sólo corre con <see cref="ManualStartVerdict.Available"/>, bajo el candado.</param>
    public async Task<ManualStartVerdict> ContinueAsync(
        Guid tenantId, Guid conversationId, Func<CancellationToken, Task> commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(conversationId, cancellationToken))
        {
            return ManualStartVerdict.Busy;
        }

        var live = await runs.FindLiveForUpdateAsync(tenantId, conversationId, cancellationToken);
        var (verdict, conversation, chatbot) = await DecideAsync(tenantId, conversationId, _ => Task.FromResult(live), cancellationToken);
        if (verdict != ManualStartVerdict.Available)
        {
            return verdict;
        }

        if (!await CanContinueAsync(tenantId, conversationId, conversation!, live, chatbot!, cancellationToken))
        {
            return ManualStartVerdict.CannotContinue;
        }

        // La versión es la de la corrida, no la publicada (D-C1), y se lee ya con el candado: el resumen no la lee para no cargar el flujo
        // en cada detalle (D-12). Si no se puede leer, el siguiente entrante fallaría, así que no se promete nada.
        if (await LoadFlowAsync(tenantId, live!.ChatbotVersionId, cancellationToken) is null)
        {
            return ManualStartVerdict.CannotContinue;
        }

        live.KeepWaiting(clock.UtcNow);
        await commit(cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManualStartVerdict.Available;
    }

    /// <summary>Spec 2026-11-01 D-4, con <see cref="ManualStartVerdict.Available"/> ya decidido: la regla de <see cref="BotAttendance"/>, el
    /// chatbot de la corrida atiende la conexión (P2-R26 sólo aplica al arrancar) y ningún asesor la tuvo desde la pausa (D-5). La consulta
    /// de eventos va al final: corre sólo si el resto ya dio verdadero.</summary>
    private async Task<bool> CanContinueAsync(
        Guid tenantId, Guid conversationId, AutomationConversationState conversation, ConversationRun? run, ServingChatbot chatbot,
        CancellationToken cancellationToken) =>
        BotAttendance.CanContinue(conversation, run)
        && chatbot.ChatbotId == run!.ChatbotId
        && !await conversations.WasTakenSinceAsync(tenantId, conversationId, conversation.AutomationPausedAt!.Value, cancellationToken);

    /// <param name="commit">Recibe el chatbot; sólo corre con <see cref="ManualStartVerdict.Available"/>, bajo el candado.</param>
    public async Task<ManualStartVerdict> StartAsync(
        Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(conversationId, cancellationToken))
        {
            return ManualStartVerdict.Busy;
        }

        var live = await runs.FindLiveForUpdateAsync(tenantId, conversationId, cancellationToken);
        var (verdict, _, chatbot) = await DecideAsync(tenantId, conversationId, _ => Task.FromResult(live), cancellationToken);
        if (verdict != ManualStartVerdict.Available)
        {
            return verdict;
        }

        // La versión publicada se lee ya con el candado: si no se puede leer, no hay chatbot que iniciar.
        if (await LoadFlowAsync(tenantId, chatbot!.PublishedVersionId, cancellationToken) is null)
        {
            return ManualStartVerdict.NoChatbot;
        }

        // Antes del commit: HumanTookOver se lee sobre el estado de antes de que Mensajería lo cambie.
        if (live is not null)
        {
            var now = clock.UtcNow;
            live.Finish(RunStatus.Interrupted, null, now);
            recorder.WriteClosed(live, null, RunCloseReasons.HumanTookOver, now);
        }

        await commit(chatbot.ChatbotId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManualStartVerdict.Available;
    }

    /// <param name="loadLive">La corrida viva, perezosa: <see cref="EvaluateAsync"/> la lee de la última y <see cref="StartAsync"/> ya la
    /// tiene bloqueada. Sólo se llama si la decisión llega a <see cref="BotAttendance.IsAttending"/>.</param>
    private async Task<(ManualStartVerdict Verdict, AutomationConversationState? Conversation, ServingChatbot? Chatbot)> DecideAsync(
        Guid tenantId, Guid conversationId, Func<CancellationToken, Task<ConversationRun?>> loadLive, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetAsync(tenantId, conversationId, cancellationToken);
        if (conversation is null)
        {
            return (ManualStartVerdict.NotFound, null, null);
        }

        // Lo barato primero: el resumen de la conversación lo llama en cada detalle, así que lo que sólo depende de la conversación
        // se decide antes de leer la corrida.
        if (conversation.Status != ConversationStates.Open)
        {
            return (ManualStartVerdict.NotOpen, conversation, null);
        }

        if (conversation.AssignedMemberId is not null)
        {
            return (ManualStartVerdict.HasAdvisor, conversation, null);
        }

        if (conversation.Blocked)
        {
            return (ManualStartVerdict.Blocked, conversation, null);
        }

        if (conversation.Channel != "WhatsApp")
        {
            return (ManualStartVerdict.UnsupportedChannel, conversation, null);
        }

        if (!await modules.IsEnabledAsync(tenantId, cancellationToken))
        {
            return (ManualStartVerdict.ModuleDisabled, conversation, null);
        }

        if (BotAttendance.IsAttending(conversation, await loadLive(cancellationToken)))
        {
            return (ManualStartVerdict.BotAttending, conversation, null);
        }

        if (await inbox.HasPendingManualStartAsync(tenantId, conversationId, cancellationToken))
        {
            return (ManualStartVerdict.StartPending, conversation, null);
        }

        // Antes que la ventana: es lo que el tenant puede arreglar.
        var chatbot = await chatbots.FindServingByConnectionAsync(tenantId, conversation.ConnectionId, cancellationToken);
        if (chatbot is not { Status: ChatbotStatus.Active })
        {
            return (ManualStartVerdict.NoChatbot, conversation, null);
        }

        if (conversation.WindowExpiresAt is not { } expiresAt || expiresAt <= clock.UtcNow)
        {
            return (ManualStartVerdict.WindowClosed, conversation, chatbot);
        }

        return (ManualStartVerdict.Available, conversation, chatbot);
    }

    private async Task<FlowDefinition?> LoadFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken)
    {
        if (await chatbots.GetVersionFlowAsync(tenantId, versionId, cancellationToken) is not { } json)
        {
            return null;
        }

        try
        {
            return FlowJson.Parse(json);
        }
        catch (FlowFormatException)
        {
            return null;
        }
    }
}
