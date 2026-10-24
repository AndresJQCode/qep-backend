using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-21 §5.2: una fila del inbox, en una unidad de trabajo. La transacción de Automations la abre <see cref="ProcessAsync"/>
/// con el lock de la conversación primero (P2-R20, sin esperarlo: P2-R45) y se confirma una sola vez al final; los cambios a la
/// conversación (arrancar, traspasar, resolver, terminar) los confirma Mensajería por su lado en el momento (Task 5), así que un
/// reintento de la fila puede encontrarlos ya hechos y lo tiene en cuenta: la corrida nueva usa el id que deriva del entrante y los
/// envíos el mismo clientId (P2-R5). Los efectos del motor los ejecuta <see cref="RunEffects"/>. El barrido de abandonadas (D-M13) también pasa por acá, con el
/// mismo orden de locks.
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
    IAutomationsModuleStatus modules,
    IAutomationsAuditRecorder audit,
    RunEffects effects,
    IClock clock,
    ILogger<RunProcessor> logger)
{
    /// <summary>D-M4: la ingesta sube la versión de la conversación por SQL; el choque se resuelve volviendo a intentar enseguida.</summary>
    private static readonly TimeSpan ConcurrencyRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Ruling P2-R45: otra unidad tiene el lock de la conversación; se vuelve a mirar en 5 s en vez de esperarla.</summary>
    private static readonly TimeSpan LockedRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>D-M13: una corrida en espera más que esto sin entrante está abandonada.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(24);

    /// <summary>Las candidatas que <see cref="FindAbandonedAsync"/> devuelve por pasada.</summary>
    public const int AbandonBatchSize = 100;

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation inbox row {MessageId} of tenant {TenantId} was re-claimed after attempt {Attempts}; this result does not count.")]
    private static partial void LogReclaimed(ILogger logger, Guid tenantId, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chatbot version {VersionId} of tenant {TenantId} has a flow that cannot be read; the bot does not run it.")]
    private static partial void LogUnreadableFlow(ILogger logger, Guid tenantId, Guid versionId);

    /// <summary>
    /// Devuelve el resultado escrito en la fila, o <see cref="RunOutcomes.Retry"/> / <see cref="RunOutcomes.Locked"/> si la fila se
    /// reprogramó (un cambio concurrente de la conversación, o su lock tomado por otra unidad). Una excepción no prevista sale de acá con
    /// la transacción ya deshecha: la fila queda para su reintento por lease (a los 5 intentos Mensajería la cierra <c>failed</c>).
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
            return await RetryLaterAsync(item, ConcurrencyRetryDelay, RunOutcomes.Retry);
        }

        if (outcome == RunOutcomes.Locked)
        {
            // P2-R45: cada reprogramación gasta un intento (lo suma el reclamo). Si la unidad original sigue corriendo pasados ~45 s
            // (lease de 30 s + tres reintentos de 5 s), al quinto Mensajería cierra la fila «failed» mientras ella termina: su trabajo
            // queda hecho una sola vez (su CompleteAsync con el testigo viejo ya no cuenta) y sólo el outcome de la fila miente. Si el
            // lock se suelta antes, el reintento que entra ve el marcador de P2-R24 y la cierra «duplicate» sin repetir nada.
            return await RetryLaterAsync(item, LockedRetryDelay, RunOutcomes.Locked);
        }

        // Sin cancelación: la unidad ya se confirmó, y una fila que no se cierra se volvería a procesar como un entrante nuevo.
        if (!await inbox.CompleteAsync(item.TenantId, item.MessageId, item.Attempts, outcome, CancellationToken.None))
        {
            LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
        }

        return outcome;
    }

    private async Task<string> RetryLaterAsync(InboundQueueItem item, TimeSpan delay, string outcome)
    {
        if (!await inbox.RetryLaterAsync(item.TenantId, item.MessageId, item.Attempts, delay, CancellationToken.None))
        {
            LogReclaimed(logger, item.TenantId, item.MessageId, item.Attempts);
        }

        return outcome;
    }

    private async Task<string> ProcessUnitAsync(InboundQueueItem item, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // P2-R45: sin esperar. Quien lo tiene es la unidad de esta misma fila, reclamada otra vez porque pasó su lease (el reclamo de
        // Mensajería sólo entrega la más vieja pendiente de cada conversación); esperarla dejaría a este worker colgado detrás.
        if (!await unitOfWork.TryLockConversationAsync(item.ConversationId, cancellationToken))
        {
            return RunOutcomes.Locked;
        }

        var run = await runs.FindLiveForUpdateAsync(item.TenantId, item.ConversationId, cancellationToken);

        // Ruling P2-R24: la unidad de este entrante ya se confirmó (el cierre de la fila falló o el lease venció antes de cerrarla). Sin
        // esto el reintento lo tomaría por una respuesta nueva, o arrancaría otra corrida con el mismo RunIdFor. Nada se toca.
        var latest = run ?? await runs.FindLatestAsync(item.TenantId, item.ConversationId, cancellationToken);
        if (latest?.LastInboundMessageId == item.MessageId)
        {
            return RunOutcomes.Duplicate;
        }

        var conversation = await conversations.GetAsync(item.TenantId, item.ConversationId, cancellationToken);
        // Después del lock y de leer la conversación: si una persona la devolvió al asistente mientras la unidad esperaba el lock, la
        // corrida nueva no puede quedar con un StartedAt anterior a ese regreso (P2-R25), o el siguiente entrante la interrumpiría.
        var now = clock.UtcNow;

        if (!await modules.IsEnabledAsync(item.TenantId, cancellationToken))
        {
            return await StandDownAsync(item, conversation, run, now, cancellationToken);
        }

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

    /// <summary>
    /// Ruling P2-R42: el tenant apagó <c>automations</c> (la ingesta sigue encolando porque sólo mira <c>messaging</c>). El bot no
    /// contesta: la corrida viva termina <c>Interrupted</c>, como cuando una persona interviene (D-M15), con el entrante marcado (P2-R24);
    /// una conversación con el asistente activo vuelve a None (D-M14) para que la bandeja no la muestre atendida por un bot mudo; una en
    /// pausa queda como está (ya la tiene una persona). Sin auditoría: no es algo que el bot haya hecho.
    /// </summary>
    private async Task<string> StandDownAsync(
        InboundQueueItem item, AutomationConversationState? conversation, ConversationRun? run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (run is not null)
        {
            run.Finish(RunStatus.Interrupted, null, now);
            run.MarkInbound(item.MessageId);
        }

        if (conversation?.AutomationStatus == AutomationStates.Active)
        {
            await conversations.EndAsync(item.TenantId, item.ConversationId, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return RunOutcomes.Skipped;
    }

    /// <summary>
    /// D-M13: las corridas que esperan desde antes de <c>now − 24 h</c>, para que el worker abandone cada una en su unidad. El
    /// <c>FOR UPDATE SKIP LOCKED</c> de <see cref="IConversationRunRepository.ClaimAbandonedAsync"/> sólo elige: salta las que una unidad
    /// tiene tomadas y se suelta enseguida (la transacción se deshace al disponerse). Abandonar con esa fila tomada y pedir después el lock
    /// de la conversación invertiría el orden de P2-R20 contra una unidad que ya tiene el lock y espera la fila: un deadlock.
    /// </summary>
    public async Task<IReadOnlyList<AbandonedRun>> FindAbandonedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var claimed = await runs.ClaimAbandonedAsync(now - AbandonAfter, AbandonBatchSize, cancellationToken);
        return [.. claimed.Select(run => new AbandonedRun(run.TenantId, run.ConversationId, run.Id))];
    }

    /// <summary>
    /// D-M13 y D-M14: abandona una candidata de <see cref="FindAbandonedAsync"/> en su propia unidad, con el orden de locks de
    /// <see cref="ProcessAsync"/> (P2-R20). Antes de tocarla se vuelve a mirar bajo el lock: si un entrante la movió entre la elección y
    /// el lock, ya no está abandonada. La conversación vuelve a None primero (Mensajería confirma por su lado); si el guardado de
    /// Automations fallara después, el barrido siguiente la vuelve a encontrar y <c>EndAsync</c> ya no hace nada. <c>false</c> = no había
    /// nada que abandonar.
    /// </summary>
    public async Task<bool> AbandonAsync(AbandonedRun candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // P2-R45: el barrido tampoco espera. Una unidad en curso es un entrante de esta conversación: la corrida ya no está abandonada, y si
        // lo siguiera estando, el barrido siguiente la vuelve a encontrar.
        if (!await unitOfWork.TryLockConversationAsync(candidate.ConversationId, cancellationToken))
        {
            return false;
        }

        var run = await runs.FindLiveForUpdateAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
        if (run is null || run.Id != candidate.RunId || run.Status != RunStatus.Waiting || run.UpdatedAt >= now - AbandonAfter)
        {
            return false;
        }

        // D-M15: una persona tomó la conversación, la resolvió o la devolvió al asistente (P2-R25) mientras la corrida esperaba. Es una acción
        // humana, así que la corrida termina Interrupted como en ProcessUnitAsync, sin auditoría, y la conversación no se toca: una devuelta
        // queda Active para que el siguiente entrante arranque desde el primer nodo. Leerla va después del lock (P2-R20).
        var conversation = await conversations.GetAsync(run.TenantId, run.ConversationId, cancellationToken);
        if (conversation is null || HumanTookOver(conversation, run))
        {
            run.Finish(RunStatus.Interrupted, null, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        try
        {
            // EndAsync no hace nada si el asistente ya no está activo (una persona la pausó o la resolvió y nadie volvió a escribir).
            await conversations.EndAsync(run.TenantId, run.ConversationId, cancellationToken);
        }
        catch (ResourceNotFoundException)
        {
            // La conversación ya no existe: no hay qué devolver a None, pero la corrida igual tiene que dejar de esperar.
        }

        run.Finish(RunStatus.Abandoned, null, now);
        audit.Record(run.TenantId, Guid.Empty, bySystem: true, AutomationsAuditActions.RunAbandoned, AutomationsAuditActions.ResourceRun, run.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
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
            return await ContinueAsync(item, conversation, run, now, cancellationToken);
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

    /// <summary>§5.2 paso 4: la corrida viva sigue con la versión con que empezó (D-C1), salvo la palabra de reinicio con una versión más
    /// nueva publicada (<see cref="RestartWithLatestAsync"/>).</summary>
    private async Task<string?> ContinueAsync(
        InboundQueueItem item, AutomationConversationState conversation, ConversationRun run, DateTimeOffset now, CancellationToken cancellationToken)
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

            var message = await CustomerMessageAsync(item, inbound, flow, run.CurrentNodeId, cancellationToken);
            if (FlowEngine.IsRestart(settings, message)
                && await RestartWithLatestAsync(item, conversation, run, now, cancellationToken) is { } restarted)
            {
                return restarted;
            }

            input = message;
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
    /// Reinicio con la última versión publicada (2026-10-23). D-C1 deja la corrida viva en la versión con que empezó para que publicar no
    /// le cambie el flujo a nadie a mitad de camino; la palabra de reinicio, en cambio, es un comienzo explícito del cliente. Si el chatbot
    /// que atiende la conexión es el de la corrida, está activo y publicó otra versión que se puede leer, la corrida termina
    /// <c>Completed</c> (nada falló: el cliente decidió empezar de nuevo; se audita como todo <c>Completed</c>) y en la misma unidad arranca
    /// otra con la publicada, con el id que deriva del entrante (P2-R5). La conversación no pasa por <c>EndAsync</c>: sigue <c>Active</c>
    /// con el mismo chatbot, como en <see cref="ResumeAsync"/>. <c>null</c> = sin versión nueva; el motor reinicia la misma corrida (D-I5).
    /// </summary>
    private async Task<string?> RestartWithLatestAsync(
        InboundQueueItem item, AutomationConversationState conversation, ConversationRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await chatbots.FindServingByConnectionAsync(item.TenantId, item.ConnectionId, cancellationToken) is not { Status: ChatbotStatus.Active } chatbot
            || chatbot.ChatbotId != run.ChatbotId
            || chatbot.PublishedVersionId == run.ChatbotVersionId
            || await LoadFlowAsync(item.TenantId, chatbot.PublishedVersionId, cancellationToken) is not { } flow)
        {
            return null;
        }

        run.Finish(RunStatus.Completed, null, now);
        audit.Record(item.TenantId, Guid.Empty, bySystem: true, AutomationsAuditActions.RunCompleted, AutomationsAuditActions.ResourceRun, run.Id, now);
        return await StartRunAsync(item, conversation, chatbot, flow, now, cancellationToken);
    }

    /// <summary>
    /// El entrante como lo lee el motor. D-M8: si la corrida espera en un Pedir adjunto y el archivo ya está en R2, el enlace de 15 min se
    /// firma antes del paso, con la llave que Mensajería da para este mismo mensaje (nunca una leída de las variables). Si todavía no
    /// llegó, se guarda sin url y <see cref="RunEffects"/> la firma más tarde, antes del efecto que la use (P2-R43).
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
            url = (await media.CreateDownloadUrlAsync(stored.Key, AttachmentUrls.Lifetime, cancellationToken)).AbsoluteUri;
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
        RunStatus.Failed => RunOutcomes.RunFailed,
        _ => RunOutcomes.Ended,
    };
}

/// <summary>D-M13: una corrida que el barrido eligió para abandonar; <see cref="RunProcessor.AbandonAsync"/> la vuelve a mirar bajo el
/// lock.</summary>
public sealed record AbandonedRun(Guid TenantId, Guid ConversationId, Guid RunId);
