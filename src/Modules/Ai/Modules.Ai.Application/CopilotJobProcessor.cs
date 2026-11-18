using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-11-17 (copiloto) §7 y plan fase 2 C2.3: corre el trabajo de modelo de un hilo del copiloto.
/// <list type="number">
/// <item>Fuera de toda transacción: el módulo <c>copilot</c> (no <c>ai</c>), la configuración y la credencial del proveedor que eligió el
/// tenant (C-19; nunca el otro, C-20), y el bucle con las herramientas que el miembro puede usar ahora (D-C3). Antes de cada herramienta,
/// <c>progress</c> en la respuesta pendiente con un UPDATE corto (D-C7).</item>
/// <item>La respuesta (ya pagada) se guarda en el trabajo antes del commit (<c>RememberResult</c>): un commit que falla o un proceso que muere
/// la retoman sin volver a llamar al modelo.</item>
/// <item>Un commit corto completa la respuesta (<c>Done</c> o <c>Failed</c> con su código) y toca <c>last_activity_at</c>. Un hilo borrado
/// mientras el modelo pensaba cierra el trabajo <c>Discarded</c>.</item>
/// </list>
/// Sin traspaso: un error del proveedor deja la respuesta <c>Failed</c> y la pantalla ofrece reintentar. Los errores transitorios se
/// reintentan como los del agente (D-A17). Ni el texto de la pregunta ni el de la respuesta llegan a los logs (D-C13).
/// </summary>
public sealed partial class CopilotJobProcessor(
    ICopilotRepository repository,
    IAiModelJobs jobs,
    IAiUnitOfWork unitOfWork,
    ITenantModules tenantModules,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    ITenantClock tenantClock,
    CopilotPromptBuilder promptBuilder,
    CopilotTurnRunner runner,
    ICopilotMemberNames memberNames,
    IClock clock,
    ILogger<CopilotJobProcessor> logger)
{
    /// <summary>§9: el tenant apagó el módulo <c>copilot</c> con el trabajo en la cola.</summary>
    public const string ModuleDisabled = "module_disabled";

    /// <summary>El <c>outcome</c> de un trabajo cuyo hilo (o cuya respuesta pendiente) ya no está.</summary>
    public const string ThreadGone = "thread_gone";

    /// <summary>Un commit que chocó (un renombre a la vez) se retoma pronto con la respuesta guardada.</summary>
    internal static readonly TimeSpan HeldRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot job {JobId} of tenant {TenantId} closed as {Outcome}.")]
    private static partial void LogClosed(ILogger logger, Guid tenantId, Guid jobId, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot job {JobId} of tenant {TenantId} rescheduled after {Code} (attempt {Attempts}).")]
    private static partial void LogRescheduled(ILogger logger, Guid tenantId, Guid jobId, string code, int attempts);

    public async Task ProcessAsync(ModelJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.CopilotThreadId is not { } threadId)
        {
            throw new ArgumentException("The job does not belong to a copilot thread.", nameof(job));
        }

        AgentTurnOutcome outcome;
        if (job.ResultJson is { } held)
        {
            outcome = StoredCopilotOutcome.Read(held);
        }
        else
        {
            var thread = await repository.ReadThreadAsync(job.TenantId, threadId, cancellationToken);
            if (thread is null || !thread.Messages.Any(message => message.Status == CopilotMessageStatus.Pending && message.JobId == job.Id))
            {
                await CloseAsync(job, ModelJobStatus.Discarded, ThreadGone, null);
                return;
            }

            var computed = await RunAsync(job, thread, cancellationToken);
            if (computed is null)
            {
                return;
            }

            // Guardada antes del commit: lo pagado no se vuelve a pagar. Sin cancelación: los tokens ya se gastaron.
            outcome = computed;
            job.RememberResult(StoredCopilotOutcome.Write(outcome));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            await CommitAsync(job, threadId, outcome, cancellationToken);
        }
        catch (RequestConcurrencyException exception)
        {
            await jobs.RetryHeldAsync(job.Id, clock.UtcNow + HeldRetryDelay, CancellationToken.None);
            LogRescheduled(logger, job.TenantId, job.Id, exception.Code, job.Attempts);
        }
    }

    /// <summary>La etapa larga. <c>null</c> si el trabajo quedó reprogramado por un error transitorio.</summary>
    private async Task<AgentTurnOutcome?> RunAsync(ModelJob job, CopilotThread thread, CancellationToken cancellationToken)
    {
        if (job.Attempts > ModelJob.MaxClaims)
        {
            // Reclamado demasiadas veces (un error que no es del proveedor): se cierra en vez de reclamarse para siempre.
            return new CopilotStopped(AgentHandoffCodes.JobTimedOut);
        }

        var modules = await tenantModules.FindAsync(job.TenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Copilot))
        {
            return new CopilotStopped(ModuleDisabled);
        }

        var settings = await repository.FindSettingsAsync(job.TenantId, cancellationToken);
        if (settings is not { Enabled: true, Provider: { } providerKey, Model: { } model } || !AiProvider.TryParse(providerKey, out var provider))
        {
            return new CopilotStopped(AiErrorCodes.CopilotDisabled);
        }

        // C-19 y C-20: la cuenta del proveedor elegido o nada; nunca la del otro.
        var credential = (await credentials.ResolveAsync(job.TenantId, provider!, cancellationToken)).Credential;
        if (credential is null)
        {
            return new CopilotStopped(AiErrorCodes.ProviderNotConfigured);
        }

        // C-12: el nombre de quien pregunta, al contexto volátil; sin nombre conocido, el prompt sigue sin él.
        var names = await memberNames.FindAsync(job.TenantId, [thread.UserId], cancellationToken);
        var prompt = promptBuilder.Build(new CopilotPromptInput(
            settings.Instructions, thread.Messages, job.UpToSeq, await tenantClock.GetAsync(job.TenantId, cancellationToken),
            names.GetValueOrDefault(thread.UserId)));
        var context = new CopilotTurnContext(
            job.TenantId,
            thread.UserId,
            thread.Id,
            job.Id,
            provider!,
            model,
            catalog.Providers.FirstOrDefault(entry => entry.Provider == provider)?.Models.FirstOrDefault(entry => entry.Id == model)?.Effort,
            credential);

        var outcome = await runner.RunAsync(
            context,
            prompt,
            (tool, token) => repository.SetProgressAsync(job.TenantId, thread.Id, job.Id, tool, token),
            cancellationToken);
        if (outcome is AgentTurnRetry retry && job.Attempts < ModelJob.MaxAttempts)
        {
            job.Reschedule(clock.UtcNow + (retry.RetryAfter ?? AgentTestJobProcessor.RetryStep * job.Attempts), retry.Code);
            LogRescheduled(logger, job.TenantId, job.Id, retry.Code, job.Attempts);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        return outcome;
    }

    /// <summary>§7 paso 3: el commit corto. Relee el hilo con tracking: así trae el <c>progress</c> que el UPDATE dejó y lo limpia.</summary>
    private async Task CommitAsync(ModelJob job, Guid threadId, AgentTurnOutcome outcome, CancellationToken cancellationToken)
    {
        var thread = await repository.FindThreadForJobAsync(job.TenantId, threadId, cancellationToken);
        if (thread is null || !thread.Messages.Any(message => message.Status == CopilotMessageStatus.Pending && message.JobId == job.Id))
        {
            await CloseAsync(job, ModelJobStatus.Discarded, ThreadGone, null);
            return;
        }

        var now = clock.UtcNow;
        if (outcome is CopilotAnswered answered)
        {
            thread.CompleteAssistant(job.Id, answered.Text, answered.Citations, now);
            await CloseAsync(job, ModelJobStatus.Done, "answered", null);
            return;
        }

        var code = CopilotTurnRunner.ErrorCodeOf(outcome);
        thread.FailAssistant(job.Id, code, now);
        await CloseAsync(job, ModelJobStatus.Failed, "failed", code);
    }

    private async Task CloseAsync(ModelJob job, ModelJobStatus status, string outcome, string? errorCode)
    {
        job.Complete(status, outcome, errorCode, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        LogClosed(logger, job.TenantId, job.Id, outcome);
    }

    /// <summary>La respuesta guardada en <c>model_jobs.result</c>: el texto y las citas ya validadas, o el código del fallo. JSON propio; nunca
    /// sale a logs.</summary>
    private sealed record StoredCopilotOutcome(string Kind, string? Text, IReadOnlyList<CopilotCitation>? Citations, string? Reason, string? Code)
    {
        public static string Write(AgentTurnOutcome outcome) =>
            JsonSerializer.Serialize(
                outcome is CopilotAnswered answered
                    ? new StoredCopilotOutcome("answered", answered.Text, answered.Citations, answered.CannotAnswerReason, null)
                    : new StoredCopilotOutcome("failed", null, null, null, CopilotTurnRunner.ErrorCodeOf(outcome)),
                Json);

        public static AgentTurnOutcome Read(string json)
        {
            var stored = JsonSerializer.Deserialize<StoredCopilotOutcome>(json, Json)
                ?? throw new InvalidOperationException("The stored copilot result is empty.");
            return stored.Kind == "answered"
                ? new CopilotAnswered(stored.Text ?? string.Empty, stored.Citations ?? [], stored.Reason)
                : new CopilotStopped(stored.Code ?? AgentHandoffCodes.InvalidResponse);
        }
    }
}
