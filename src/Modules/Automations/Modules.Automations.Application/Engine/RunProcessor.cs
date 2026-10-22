using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-21 §5.2: una fila del inbox, en una unidad de trabajo. La transacción de Automations la abre <see cref="ProcessAsync"/>
/// con el lock de la conversación primero (P2-R20) y se confirma una sola vez al final; los cambios a la conversación (arrancar,
/// traspasar, resolver, terminar) los confirma Mensajería por su lado en el momento (Task 5), así que un reintento de la fila puede
/// encontrarlos ya hechos y lo tiene en cuenta: la corrida nueva usa el id que deriva del entrante y los envíos el mismo clientId
/// (P2-R5). Los efectos del motor los ejecuta <see cref="RunEffects"/>.
/// </summary>
public sealed partial class RunProcessor(
    IAutomationsUnitOfWork unitOfWork,
    IConversationRunRepository runs,
    IChatbotRepository chatbots,
    IAutomationInboundQueue inbox,
    IAutomationConversations conversations,
    IAutomationTenantNames tenantNames,
    IAutomationConnections connections,
    IAutomationBusinessHours businessHours,
    IAutomationMedia media,
    IAutomationsAuditRecorder audit,
    RunEffects effects,
    IClock clock,
    ILogger<RunProcessor> logger)
{
    /// <summary>D-M4: la ingesta sube la versión de la conversación por SQL; el choque se resuelve volviendo a intentar enseguida.</summary>
    private static readonly TimeSpan ConcurrencyRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>D-M8: <c>{saveTo.url}</c> vale 15 minutos.</summary>
    private static readonly TimeSpan AttachmentUrlLifetime = TimeSpan.FromMinutes(15);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation inbox row {MessageId} of tenant {TenantId} was re-claimed after attempt {Attempts}; this result does not count.")]
    private static partial void LogReclaimed(ILogger logger, Guid tenantId, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chatbot version {VersionId} of tenant {TenantId} has a flow that cannot be read; the bot does not run it.")]
    private static partial void LogUnreadableFlow(ILogger logger, Guid tenantId, Guid versionId);

    /// <summary>
    /// Devuelve el resultado escrito en la fila, o <see cref="RunOutcomes.Retry"/> si un cambio concurrente de la conversación la
    /// reprogramó. Una excepción no prevista sale de acá con la transacción ya deshecha: la fila queda para su reintento por lease (a los
    /// 5 intentos Mensajería la cierra <c>failed</c>).
    /// </summary>
    public async Task<string> ProcessAsync(InboundQueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        string outcome;
        try
        {
            outcome = await ProcessUnitAsync(item, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            if (!await inbox.RetryLaterAsync(item.TenantId, item.MessageId, item.Attempts, ConcurrencyRetryDelay, CancellationToken.None))
            {
                LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
            }

            return RunOutcomes.Retry;
        }

        // Sin cancelación: la unidad ya se confirmó, y una fila que no se cierra se volvería a procesar como un entrante nuevo.
        if (!await inbox.CompleteAsync(item.TenantId, item.MessageId, item.Attempts, outcome, CancellationToken.None))
        {
            LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
        }

        return outcome;
    }

    private async Task<string> ProcessUnitAsync(InboundQueueItem item, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockConversationAsync(item.ConversationId, cancellationToken);
        var run = await runs.FindLiveForUpdateAsync(item.TenantId, item.ConversationId, cancellationToken);

        // Ruling P2-R24: la unidad de este entrante ya se confirmó (el cierre de la fila falló o el lease venció antes de cerrarla). Sin
        // esto el reintento lo tomaría por una respuesta nueva, o arrancaría otra corrida con el mismo RunIdFor. Nada se toca.
        var latest = run ?? await runs.FindLatestAsync(item.TenantId, item.ConversationId, cancellationToken);
        if (latest?.LastInboundMessageId == item.MessageId)
        {
            return RunOutcomes.Duplicate;
        }

        var conversation = await conversations.GetAsync(item.TenantId, item.ConversationId, cancellationToken);

        // D-M15: una acción humana termina la corrida viva; Mensajería no conoce Automations, así que se detecta acá.
        var interrupted = false;
        if (run is not null && (conversation is null || HumanTookOver(conversation, run)))
        {
            run.Finish(RunStatus.Interrupted, null, now);
            run.MarkInbound(item.MessageId);
            run = null;
            interrupted = true;
        }

        var outcome = conversation is null ? null : await DecideAsync(item, conversation, run, now, cancellationToken);
        // Guardar confirma la transacción y suelta los locks (IAutomationsUnitOfWork): una sola vez por unidad.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return outcome ?? (interrupted ? RunOutcomes.Interrupted : RunOutcomes.Skipped);
    }

    /// <summary>D-M15. Tomar y devolver al asistente no deja rastro en el estado (tomar pausa y no encola, devolver vuelve a
    /// <c>Active</c>): por eso P2-R25 compara la hora en que una persona la devolvió con el arranque de la corrida.</summary>
    private static bool HumanTookOver(AutomationConversationState conversation, ConversationRun run) =>
        conversation.AssignedMemberId is not null
        || conversation.Status == ConversationStates.Resolved
        || conversation.AutomationStatus is AutomationStates.Paused or AutomationStates.None
        || conversation.AutomationResumedAt > run.StartedAt;

    /// <summary>§5.2 pasos 2 a 4; <c>null</c> = el entrante no le toca al bot.</summary>
    private async Task<string?> DecideAsync(
        InboundQueueItem item, AutomationConversationState conversation, ConversationRun? run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // D-M3: sólo una conversación abierta y sin asesor.
        if (conversation.Status != ConversationStates.Open || conversation.AssignedMemberId is not null)
        {
            return null;
        }

        if (run is not null)
        {
            return await ContinueAsync(item, run, now, cancellationToken);
        }

        return conversation.AutomationStatus switch
        {
            AutomationStates.None => await StartByTriggerAsync(item, conversation, now, cancellationToken),
            AutomationStates.Active => await ResumeAsync(item, conversation, now, cancellationToken),
            _ => null,
        };
    }

    /// <summary>§5.2 paso 3, D-C6, D-M4 y D-M16: sin corrida y en <c>None</c>, el disparador del chatbot activo de la conexión.</summary>
    private async Task<string?> StartByTriggerAsync(
        InboundQueueItem item, AutomationConversationState conversation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await chatbots.FindServingByConnectionAsync(item.TenantId, item.ConnectionId, cancellationToken) is not { Status: ChatbotStatus.Active } chatbot
            || await inbox.ReadAsync(item.TenantId, item.MessageId, cancellationToken) is not { } inbound)
        {
            return null;
        }

        // El horario sólo se consulta cuando decide algo.
        var hoursOpen = chatbot.Trigger != ChatbotTrigger.OutsideHours || !item.Opened
            || await businessHours.IsOpenAsync(item.TenantId, item.ConnectionId, cancellationToken);
        if (!TriggerEvaluator.Applies(chatbot.Trigger, chatbot.Keywords, item.Opened, inbound.Text, hoursOpen)
            || await LoadFlowAsync(item.TenantId, chatbot.PublishedVersionId, cancellationToken) is not { } flow)
        {
            return null;
        }

        try
        {
            await conversations.StartAsync(item.TenantId, item.ConversationId, chatbot.ChatbotId, firstStepTitle: null, cancellationToken);
        }
        catch (DomainException)
        {
            // Una persona la tomó o la resolvió entre la lectura y el arranque: ya no es del bot. Un concurrency.conflict, en cambio, sube
            // a ProcessAsync y reprograma la fila.
            return null;
        }

        return await StartRunAsync(item, conversation, chatbot, flow, now, cancellationToken);
    }

    /// <summary>
    /// D-M15: «Devolver al asistente» la dejó <c>Active</c> sin corrida; arranca desde el primer nodo sin disparador ni
    /// <c>StartAsync</c> (ya está activa). Es también el camino del reintento de una unidad que arrancó en Mensajería y se deshizo en
    /// Automations. Si el chatbot de la conexión ya no atiende, la conversación vuelve a <c>None</c> (D-M14).
    /// </summary>
    private async Task<string?> ResumeAsync(
        InboundQueueItem item, AutomationConversationState conversation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await chatbots.FindServingByConnectionAsync(item.TenantId, item.ConnectionId, cancellationToken) is not { Status: ChatbotStatus.Active } chatbot
            || await LoadFlowAsync(item.TenantId, chatbot.PublishedVersionId, cancellationToken) is not { } flow)
        {
            await conversations.EndAsync(item.TenantId, item.ConversationId, cancellationToken);
            return null;
        }

        // P2-R26: la conexión puede servirla ya otro chatbot que el que la conversación tenía cuando una persona la devolvió.
        if (conversation.AutomationChatbotId != chatbot.ChatbotId)
        {
            await conversations.SetChatbotAsync(item.TenantId, item.ConversationId, chatbot.ChatbotId, cancellationToken);
        }

        return await StartRunAsync(item, conversation, chatbot, flow, now, cancellationToken);
    }

    /// <summary>D-M17: la corrida nueva entra sólo con <see cref="EngineInput.Start"/>; el entrante que disparó no es una respuesta.</summary>
    private async Task<string> StartRunAsync(
        InboundQueueItem item, AutomationConversationState conversation, ServingChatbot chatbot, FlowDefinition flow, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenantName = await tenantNames.FindAsync(item.TenantId, cancellationToken);
        var connection = (await connections.ListAsync(item.TenantId, cancellationToken)).FirstOrDefault(candidate => candidate.Id == item.ConnectionId);
        var variables = FixedVariables.Build(conversation.CustomerName, conversation.Phone, tenantName, connection?.Name ?? string.Empty);
        var run = ConversationRun.Start(
            DeterministicClientId.RunIdFor(item.MessageId), item.TenantId, item.ConversationId, item.ConnectionId, chatbot.ChatbotId,
            chatbot.PublishedVersionId, RunState.Initial(variables), now);
        run.MarkInbound(item.MessageId);
        await runs.AddAsync(run, cancellationToken);
        audit.Record(item.TenantId, Guid.Empty, bySystem: true, AutomationsAuditActions.RunStarted, AutomationsAuditActions.ResourceRun, run.Id, now);

        var settings = new EngineSettings(chatbot.RestartKeyword, chatbot.MaxAttempts);
        await effects.RunAsync(new RunUnit(run, flow, settings, BaseStepNumber: 0), new EngineInput.Start(), now, cancellationToken);
        return OutcomeOf(run, RunOutcomes.Started);
    }

    /// <summary>§5.2 paso 4: la corrida viva sigue con la versión con que empezó (D-C1).</summary>
    private async Task<string?> ContinueAsync(InboundQueueItem item, ConversationRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        run.MarkInbound(item.MessageId);
        // Ruling P2-R6: un chatbot borrado mientras la corrida esperaba (la carrera del plan 1) se lleva sus versiones.
        var flow = await LoadFlowAsync(item.TenantId, run.ChatbotVersionId, cancellationToken);
        var settings = await chatbots.FindEngineSettingsAsync(item.TenantId, run.ChatbotId, cancellationToken);
        if (flow is null || settings is null)
        {
            await effects.FailAsync(run, run.ToState(), AutomationsRunFailures.VersionMissing, now, cancellationToken);
            return OutcomeOf(run, RunOutcomes.Advanced);
        }

        var unit = new RunUnit(run, flow, settings, run.StepNumber);

        EngineInput input;
        if (run.Status == RunStatus.Waiting)
        {
            if (await inbox.ReadAsync(item.TenantId, item.MessageId, cancellationToken) is not { } inbound)
            {
                return null;
            }

            input = await CustomerMessageAsync(item, inbound, flow, run.CurrentNodeId, cancellationToken);
        }
        else if (await effects.PendingInputAsync(unit, cancellationToken) is { } pending)
        {
            // Running: sólo queda así si una unidad murió a mitad de un efecto externo; se vuelve a pedir el del bloque actual.
            input = pending;
        }
        else
        {
            await effects.FailAsync(run, run.ToState(), AutomationsRunFailures.Of(EndReasons.TargetMissing), now, cancellationToken);
            return OutcomeOf(run, RunOutcomes.Advanced);
        }

        await effects.RunAsync(unit, input, now, cancellationToken);
        return OutcomeOf(run, RunOutcomes.Advanced);
    }

    /// <summary>
    /// El entrante como lo lee el motor. D-M8: si la corrida espera en un Pedir adjunto y el archivo ya está en R2, el enlace de 15 min se
    /// firma antes del paso, con la llave que Mensajería da para este mismo mensaje (nunca una leída de las variables).
    /// </summary>
    private async Task<EngineInput.CustomerMessage> CustomerMessageAsync(
        InboundQueueItem item, InboundMessage inbound, FlowDefinition flow, string? waitingNodeId, CancellationToken cancellationToken)
    {
        if (inbound.Attachment is not { } attachment)
        {
            return new EngineInput.CustomerMessage(inbound.Kind, inbound.Text, inbound.ReplyId, null);
        }

        string? url = null;
        if (attachment.Stored
            && flow.Nodes.Any(node => node.Id == waitingNodeId && node is AttachmentNode)
            && await media.TryGetStoredAsync(item.TenantId, item.MessageId, cancellationToken) is { } stored)
        {
            url = (await media.CreateDownloadUrlAsync(stored.Key, AttachmentUrlLifetime, cancellationToken)).AbsoluteUri;
        }

        return new EngineInput.CustomerMessage(
            inbound.Kind, inbound.Text, inbound.ReplyId, new AttachmentInput(inbound.MessageId, attachment.FileName, attachment.MimeType, attachment.Stored, url));
    }

    /// <summary><c>null</c> si la versión no existe o no se puede leer (una guardada corrupta): como en P2-R13/R14, lanzar reintentaría la
    /// fila cinco veces y dejaría la conversación con un bot mudo; sin flujo, una corrida viva termina Failed y pasa a un asesor.</summary>
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
            LogUnreadableFlow(logger, tenantId, versionId);
            return null;
        }
    }

    private static string OutcomeOf(ConversationRun run, string whenLive) => run.Status switch
    {
        RunStatus.Running or RunStatus.Waiting => whenLive,
        RunStatus.Interrupted => RunOutcomes.Interrupted,
        RunStatus.Failed => RunOutcomes.Failed,
        _ => RunOutcomes.Ended,
    };
}
