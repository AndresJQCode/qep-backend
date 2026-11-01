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
}

/// <summary>
/// Spec 2026-10-31 D-6 y D-7: decide y resguarda el arranque manual del asistente. <see cref="EvaluateAsync"/> es una lectura sin
/// transacción (el resumen de la conversación la llama en cada detalle); <see cref="StartAsync"/> repite la misma decisión bajo el
/// candado de la conversación, cierra la corrida muerta y deja que Mensajería confirme el cambio en el mismo guardado.
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
    public async Task<ManualStartVerdict> EvaluateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var latest = await runs.FindLatestAsync(tenantId, conversationId, cancellationToken);
        var live = latest is { IsLive: true } ? latest : null;
        return (await DecideAsync(tenantId, conversationId, live, cancellationToken)).Verdict;
    }

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
        var (verdict, _, chatbot) =await DecideAsync(tenantId, conversationId, live, cancellationToken);
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

    private async Task<(ManualStartVerdict Verdict, AutomationConversationState? Conversation, ServingChatbot? Chatbot)> DecideAsync(
        Guid tenantId, Guid conversationId, ConversationRun? live, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetAsync(tenantId, conversationId, cancellationToken);
        if (conversation is null)
        {
            return (ManualStartVerdict.NotFound, null, null);
        }

        // Lo barato primero: el resumen de la conversación lo llama en cada detalle.
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

        if (BotAttendance.IsAttending(conversation, live))
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
