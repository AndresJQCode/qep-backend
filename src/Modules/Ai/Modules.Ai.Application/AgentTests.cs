using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

/// <summary>Las pruebas del playground y su actividad (spec 2026-10-29 §7).</summary>
public interface IAgentTestRepository
{
    /// <summary>Con tracking, sólo si es del tenant. Una vencida también vuelve: quien llama decide.</summary>
    Task<AgentTest?> FindAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken);

    void Add(AgentTest test);

    void Remove(AgentTest test);

    /// <summary>Las herramientas que llamaron los trabajos de la prueba y su consumo, por proveedor.</summary>
    Task<AgentTestActivity> LoadActivityAsync(Guid tenantId, Guid testId, CancellationToken cancellationToken);
}

public sealed record AgentTestActivity(IReadOnlyList<AgentTestToolCallDto> ToolCalls, IReadOnlyList<AgentTestUsageDto> Usage);

/// <summary>La cola <c>ai.model_jobs</c> (D-A13).</summary>
public interface IAiModelJobs
{
    void Add(ModelJob job);

    /// <summary>Un trabajo vencido (pendiente, o en curso con el lease vencido) pasa a <c>Running</c> con <paramref name="lease"/> y un intento
    /// más, con <c>SKIP LOCKED</c>: dos workers nunca toman el mismo. De pruebas y de sesiones.</summary>
    Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Revisión final, hallazgo 2: acorta el lease de un trabajo <c>Running</c> para que se retome en <paramref name="retryAt"/>, en su
    /// propio comando y sin pasar por el change tracker. Es para después de un commit corto que se deshizo: lo que quedó a medio cambiar en
    /// el contexto (la sesión, los turnos) no se guarda.</summary>
    Task RetryHeldAsync(Guid jobId, DateTimeOffset retryAt, CancellationToken cancellationToken);
}

public sealed record CreateAgentTestCommand(Guid TenantId, Guid AgentId, Guid? CustomerId) : ICommand<AgentTestDto>;

public sealed record SendAgentTestMessageCommand(Guid TenantId, Guid TestId, string Text) : ICommand<AgentTestDto>;

public sealed record GetAgentTestQuery(Guid TenantId, Guid TestId) : IQuery<AgentTestDto>;

public sealed record DeleteAgentTestCommand(Guid TenantId, Guid TestId) : ICommand<bool>;

/// <summary>
/// §5 y §7: lo que el playground dibuja, y nada que tenga que calcular. <see cref="Pending"/> dice si sondear; la transcripción trae las
/// fuentes de cada respuesta y los traspasos con su motivo o código; <see cref="ToolCalls"/> y <see cref="Usage"/> son de los trabajos de esta
/// prueba (el consumo de una prueba se muestra aparte del de las conversaciones reales).
/// </summary>
public sealed record AgentTestDto(
    Guid Id,
    Guid AgentId,
    string Provider,
    string Model,
    Guid? CustomerId,
    DateTimeOffset ExpiresAt,
    bool Pending,
    bool HandedOff,
    IReadOnlyList<AgentTestEntryDto> Transcript,
    IReadOnlyList<AgentTestToolCallDto> ToolCalls,
    IReadOnlyList<AgentTestUsageDto> Usage);

/// <param name="Role"><c>customer</c>, <c>agent</c> o <c>handoff</c>.</param>
/// <param name="SourceIds">Los ids crudos; se conservan porque el frontend ya los lee.</param>
/// <param name="Sources">Backend for frontend (plan 2026-11-17 A4, F-4): las mismas fuentes ya resueltas, con la forma de la actividad real
/// (<see cref="AgentActivitySourceDto"/>), para que el playground muestre la pregunta, el atajo o la herramienta sin cruzar el borrador ni las
/// respuestas rápidas.</param>
public sealed record AgentTestEntryDto(
    int Seq,
    string Role,
    string? Text,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<AgentActivitySourceDto> Sources,
    string? Reason,
    string? Code,
    DateTimeOffset At);

public sealed record AgentTestToolCallDto(
    string CallId, string Tool, JsonElement Arguments, JsonElement? Result, string Outcome, string? ErrorCode, int DurationMs, DateTimeOffset At);

/// <summary>C-4: por proveedor; la caché en <c>null</c> cuando el proveedor no la reporta.</summary>
public sealed record AgentTestUsageDto(
    string Provider, int Calls, long InputTokens, long OutputTokens, long? CacheCreationInputTokens, long? CacheReadInputTokens);

public sealed class SendAgentTestMessageValidator : AbstractValidator<SendAgentTestMessageCommand>
{
    public SendAgentTestMessageValidator() =>
        RuleFor(command => command.Text).NotEmpty().MaximumLength(AgentTest.TextMaxLength)
            .Must(text => !string.IsNullOrWhiteSpace(text)).WithMessage("The message cannot be blank.");
}

internal static class AgentTestSupport
{
    public static ResourceNotFoundException NotFound(Guid testId) => new(AiErrorCodes.TestNotFound, $"Test '{testId}' was not found.");

    /// <summary>§7: la prueba exige la cuenta del proveedor del borrador, igual que publicar (C-18).</summary>
    public static async Task<AiProvider> RequireProviderAsync(
        IAiProviderCredentials credentials, Guid tenantId, AgentConfig config, CancellationToken cancellationToken)
    {
        if (!AiProvider.TryParse(config.Model.Provider, out var provider))
        {
            throw new AiDomainException(
                AiErrorCodes.AgentInvalid,
                "El modelo del borrador no tiene un proveedor válido.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["model.provider"] = ["model_provider_unknown"] });
        }

        var resolution = await credentials.ResolveAsync(tenantId, provider!, cancellationToken);
        return resolution.Credential is null ? throw new AiProviderNotConfiguredException(provider!) : provider!;
    }

    /// <summary>Las FAQ salen de la foto del borrador de la prueba, las herramientas de las llamadas de la prueba y las respuestas rápidas se
    /// leen sólo si alguna entrada tiene fuentes, como en la actividad real (<see cref="AgentSourceResolver"/>).</summary>
    public static async Task<AgentTestDto> ToDtoAsync(
        AgentTest test, AgentTestActivity activity, IAiQuickReplies quickReplies, CancellationToken cancellationToken)
    {
        var config = test.Config;
        var faq = AgentSourceResolver.FaqOf(config);
        var shortcuts = await AgentSourceResolver.ShortcutsAsync(
            quickReplies, test.TenantId, test.Transcript.Any(entry => entry.SourceIds.Count > 0), cancellationToken);
        return new AgentTestDto(
            test.Id,
            test.AgentId,
            config.Model.Provider,
            config.Model.Id,
            test.CustomerId,
            test.ExpiresAt,
            test.PendingJobId is not null,
            test.HandedOff,
            [.. test.Transcript.Select(entry => new AgentTestEntryDto(
                entry.Seq,
                entry.Role,
                entry.Text,
                entry.SourceIds,
                [.. entry.SourceIds.Select(id => AgentSourceResolver.Resolve(id, faq, shortcuts, activity.ToolCalls))],
                entry.Reason,
                entry.Code,
                entry.At))],
            activity.ToolCalls,
            activity.Usage);
    }
}

public sealed class CreateAgentTestHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiProviderCredentials credentials,
    IAgentTestRepository tests,
    IAiQuickReplies quickReplies,
    IAiUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<CreateAgentTestCommand, AgentTestDto>
{
    public async Task<AgentTestDto> HandleAsync(CreateAgentTestCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var memberId = await caller.RequireAsync(command.TenantId, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        var draft = agent.DraftConfig;
        await AgentTestSupport.RequireProviderAsync(credentials, command.TenantId, draft, cancellationToken);

        var test = AgentTest.Open(Guid.CreateVersion7(), command.TenantId, agent.Id, memberId, command.CustomerId, draft, clock.UtcNow);
        tests.Add(test);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await AgentTestSupport.ToDtoAsync(test, new AgentTestActivity([], []), quickReplies, cancellationToken);
    }
}

public sealed class SendAgentTestMessageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<SendAgentTestMessageCommand> validator,
    IAgentTestRepository tests,
    IAiModelJobs jobs,
    IAiProviderCredentials credentials,
    IAiQuickReplies quickReplies,
    IAiUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<SendAgentTestMessageCommand, AgentTestDto>
{
    public async Task<AgentTestDto> HandleAsync(SendAgentTestMessageCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var now = clock.UtcNow;
        var test = await tests.FindAsync(command.TenantId, command.TestId, cancellationToken);
        if (test is null || test.IsExpiredAt(now))
        {
            throw AgentTestSupport.NotFound(command.TestId);
        }

        if (test.PendingJobId is not null)
        {
            throw new ResourceConflictException(AiErrorCodes.TestBusy, "La prueba ya tiene un mensaje en curso.");
        }

        await AgentTestSupport.RequireProviderAsync(credentials, command.TenantId, test.Config, cancellationToken);
        var jobId = Guid.CreateVersion7();
        test.AddCustomerMessage(command.Text, jobId, now);
        jobs.Add(ModelJob.ForTest(jobId, command.TenantId, test.Id, test.Transcript[^1].Seq, now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await AgentTestSupport.ToDtoAsync(
            test, await tests.LoadActivityAsync(command.TenantId, test.Id, cancellationToken), quickReplies, cancellationToken);
    }
}

public sealed class GetAgentTestHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentTestRepository tests, IAiQuickReplies quickReplies, IClock clock)
    : IQueryHandler<GetAgentTestQuery, AgentTestDto>
{
    public async Task<AgentTestDto> HandleAsync(GetAgentTestQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentManage, cancellationToken);
        var test = await tests.FindAsync(query.TenantId, query.TestId, cancellationToken);
        if (test is null || test.IsExpiredAt(clock.UtcNow))
        {
            throw AgentTestSupport.NotFound(query.TestId);
        }

        return await AgentTestSupport.ToDtoAsync(
            test, await tests.LoadActivityAsync(query.TenantId, test.Id, cancellationToken), quickReplies, cancellationToken);
    }
}

public sealed class DeleteAgentTestHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentTestRepository tests, IAiUnitOfWork unitOfWork)
    : ICommandHandler<DeleteAgentTestCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteAgentTestCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var test = await tests.FindAsync(command.TenantId, command.TestId, cancellationToken)
            ?? throw AgentTestSupport.NotFound(command.TestId);
        tests.Remove(test);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// §7: corre un trabajo de prueba con el mismo <see cref="AgentTurnRunner"/> que una conversación real, con el proveedor <b>del borrador
/// fotografiado</b> y un canal falso: la respuesta va a la transcripción, nunca a Mensajería. Las escrituras se simulan
/// (<c>create_quotation_draft</c>). El modelo se llama fuera de toda transacción (D-A13); el guardado del final es corto.
/// </summary>
public sealed partial class AgentTestJobProcessor(
    IAgentTestRepository tests,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    IAiQuickReplies quickReplies,
    ITenantClock tenantClock,
    AgentPromptBuilder promptBuilder,
    AgentTurnRunner runner,
    IAiUnitOfWork unitOfWork,
    IClock clock,
    ILogger<AgentTestJobProcessor> logger)
{
    /// <summary>D-A17: la espera de un reintento sin <c>retry-after</c>, que crece con cada intento.</summary>
    internal static readonly TimeSpan RetryStep = TimeSpan.FromSeconds(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI test job {JobId} of tenant {TenantId} discarded: its test is gone, expired or waits for another job.")]
    private static partial void LogDiscarded(ILogger logger, Guid tenantId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI test job {JobId} of tenant {TenantId} rescheduled after {Code} (attempt {Attempts}).")]
    private static partial void LogRescheduled(ILogger logger, Guid tenantId, Guid jobId, string code, int attempts);

    public async Task ProcessAsync(ModelJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = clock.UtcNow;
        var test = job.TestId is { } testId ? await tests.FindAsync(job.TenantId, testId, cancellationToken) : null;
        if (test is null || test.PendingJobId != job.Id || test.IsExpiredAt(now))
        {
            LogDiscarded(logger, job.TenantId, job.Id);
            job.Complete(ModelJobStatus.Discarded, "test_gone", null, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var config = test.Config;
        AiCredential? credential = null;
        if (AiProvider.TryParse(config.Model.Provider, out var provider))
        {
            credential = (await credentials.ResolveAsync(job.TenantId, provider!, cancellationToken)).Credential;
        }

        if (credential is null)
        {
            Finish(test, job, new AgentTurnFailed(AgentFailureKind.ProviderFailed, AgentFailureReason.ProviderError, AiErrorCodes.ProviderNotConfigured));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var replies = config.UseQuickReplies ? await quickReplies.ListAsync(job.TenantId, cancellationToken) : [];
        var prompt = promptBuilder.Build(new AgentPromptInput(
            config, replies, HistoryOf(test), await tenantClock.GetAsync(job.TenantId, cancellationToken), WindowRemaining: null));
        var context = new AgentTurnContext(
            job.TenantId,
            test.AgentId,
            AgentVersionId: null,
            provider!,
            config.Model.Id,
            EffortOf(provider!, config.Model.Id),
            config,
            credential,
            job.Id,
            SessionId: null,
            ConversationId: null,
            TestId: test.Id,
            test.CustomerId,
            [.. config.Faq.Select(faq => faq.Id), .. replies.Select(reply => reply.Id)],
            IsTest: true);

        var outcome = await runner.RunAsync(context, prompt, cancellationToken);

        now = clock.UtcNow;
        if (outcome is AgentTurnRetry retry && job.Attempts < ModelJob.MaxAttempts)
        {
            job.Reschedule(now + (retry.RetryAfter ?? RetryStep * job.Attempts), retry.Code);
            LogRescheduled(logger, job.TenantId, job.Id, retry.Code, job.Attempts);
        }
        else
        {
            Finish(test, job, outcome);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private void Finish(AgentTest test, ModelJob job, AgentTurnOutcome outcome)
    {
        var now = clock.UtcNow;
        switch (outcome)
        {
            case AgentReplied replied:
                test.RecordReply(job.Id, replied.Text, replied.SourceIds, now);
                job.Complete(ModelJobStatus.Done, "replied", null, now);
                break;
            case AgentHandedOff handedOff:
                test.RecordHandoff(job.Id, handedOff.Reason, null, now);
                job.Complete(ModelJobStatus.Done, "handed_off", null, now);
                break;
            case AgentTurnRetry exhausted:
                test.RecordHandoff(job.Id, null, exhausted.Code, now);
                job.Complete(ModelJobStatus.Failed, "provider_failed", exhausted.Code, now);
                break;
            case AgentTurnFailed failed:
                var code = failed.Code ?? AgentHandoffCodes.For(failed.Reason);
                test.RecordHandoff(job.Id, null, code, now);
                job.Complete(ModelJobStatus.Failed, failed.Kind == AgentFailureKind.ProviderFailed ? "provider_failed" : "agent_failed", code, now);
                break;
            default:
                throw new InvalidOperationException($"Unknown outcome {outcome.GetType().Name}.");
        }
    }

    private string? EffortOf(AiProvider provider, string model) =>
        catalog.Providers.FirstOrDefault(entry => entry.Provider == provider)?.Models.FirstOrDefault(entry => entry.Id == model)?.Effort;

    private static AgentHistoryTurn[] HistoryOf(AgentTest test) =>
        [
            .. test.Transcript
                .Where(entry => entry.Role is AgentTestEntry.Customer or AgentTestEntry.Agent)
                .Select(entry => new AgentHistoryTurn(
                    entry.Role == AgentTestEntry.Customer ? AgentTurnRole.Customer : AgentTurnRole.Agent, entry.Text)),
        ];
}
