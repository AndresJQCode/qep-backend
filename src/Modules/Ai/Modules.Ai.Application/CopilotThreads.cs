using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

public sealed record ListCopilotThreadsQuery(Guid TenantId, string? Cursor) : IQuery<CopilotThreadPage>;

public sealed record CreateCopilotThreadCommand(Guid TenantId, string Text) : ICommand<CopilotThreadDto>;

public sealed record GetCopilotThreadQuery(Guid TenantId, Guid ThreadId) : IQuery<CopilotThreadDto>;

public sealed record RenameCopilotThreadCommand(Guid TenantId, Guid ThreadId, long Version, string Title) : ICommand<CopilotThreadDto>;

public sealed record DeleteCopilotThreadCommand(Guid TenantId, Guid ThreadId) : ICommand<bool>;

public sealed record SendCopilotMessageCommand(Guid TenantId, Guid ThreadId, string Text) : ICommand<CopilotThreadDto>;

public sealed record RetryCopilotMessageCommand(Guid TenantId, Guid ThreadId, Guid MessageId) : ICommand<CopilotThreadDto>;

/// <summary>§8: la lista del miembro, 30 por página, de la actividad más reciente a la más vieja. Sólo sus hilos.</summary>
public sealed record CopilotThreadPage(IReadOnlyList<CopilotThreadSummaryDto> Items, string? NextCursor);

/// <param name="Version">Adenda D-11 del plan fase 2: renombrar desde la lista manda <c>If-Match</c> sin abrir el hilo.</param>
public sealed record CopilotThreadSummaryDto(Guid Id, string Title, DateTimeOffset LastActivityAt, long Version);

/// <summary>
/// Backend for frontend (§8 y D-C7): lo que el panel del hilo dibuja. <see cref="Pending"/> dice si sondear, sin que la pantalla recorra los
/// mensajes; <see cref="Version"/> es la del <c>If-Match</c> de renombrar. Los enums viajan con su nombre (<c>User</c>, <c>Pending</c>).
/// </summary>
public sealed record CopilotThreadDto(Guid Id, string Title, IReadOnlyList<CopilotMessageDto> Messages, bool Pending, long Version);

/// <param name="Role"><c>User</c> o <c>Assistant</c>.</param>
/// <param name="Status"><c>Done</c>, <c>Pending</c> o <c>Failed</c>.</param>
/// <param name="Citations">D-C6: ya validadas contra los <c>ref</c> del trabajo; el enlace lo arma el frontend con <c>kind</c> e <c>id</c>.</param>
/// <param name="Progress">D-C7: la herramienta en curso mientras la respuesta está pendiente.</param>
/// <param name="ErrorCode">El código de una respuesta <c>Failed</c> (los del proveedor, <c>job_timed_out</c>, <c>member_inactive</c>…).</param>
public sealed record CopilotMessageDto(
    Guid Id,
    string Role,
    string Text,
    IReadOnlyList<CopilotCitation> Citations,
    string Status,
    string? Progress,
    string? ErrorCode,
    DateTimeOffset CreatedAt);

/// <summary>Plan C1.5: la posición de un hilo en el orden <c>(last_activity_at, id)</c> descendente. Mismo formato opaco que
/// <see cref="AgentSessionCursor"/>: el frontend sólo lo devuelve tal cual.</summary>
public sealed record CopilotThreadCursor(DateTimeOffset LastActivityAt, Guid Id)
{
    public static string Encode(CopilotThreadCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return AgentSessionCursor.Encode(new AgentSessionCursor(cursor.LastActivityAt, cursor.Id));
    }

    public static bool TryDecode(string? value, out CopilotThreadCursor cursor)
    {
        var decoded = AgentSessionCursor.TryDecode(value, out var inner);
        cursor = decoded ? new CopilotThreadCursor(inner.StartedAt, inner.Id) : null!;
        return decoded;
    }
}

public sealed class CreateCopilotThreadValidator : AbstractValidator<CreateCopilotThreadCommand>
{
    public CreateCopilotThreadValidator() => RuleFor(command => command.Text).CopilotQuestion();
}

public sealed class SendCopilotMessageValidator : AbstractValidator<SendCopilotMessageCommand>
{
    public SendCopilotMessageValidator() => RuleFor(command => command.Text).CopilotQuestion();
}

public sealed class RenameCopilotThreadValidator : AbstractValidator<RenameCopilotThreadCommand>
{
    public RenameCopilotThreadValidator() =>
        RuleFor(command => command.Title)
            .NotEmpty().MaximumLength(CopilotThread.TitleMaxLength)
            .Must(title => !string.IsNullOrWhiteSpace(title)).WithMessage("The title cannot be blank.");
}

public sealed class ListCopilotThreadsValidator : AbstractValidator<ListCopilotThreadsQuery>
{
    public ListCopilotThreadsValidator() =>
        RuleFor(query => query.Cursor)
            .Must(value => CopilotThreadCursor.TryDecode(value, out _)).When(query => query.Cursor is not null)
            .OverridePropertyName("cursor")
            .WithMessage("cursor is not a cursor issued by this list.");
}

internal static class CopilotValidationRules
{
    /// <summary>§5.2: la pregunta, 1..2000 caracteres y no en blanco. La clave del error es <c>Text</c>, como el cuerpo.</summary>
    public static IRuleBuilderOptions<T, string> CopilotQuestion<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(CopilotThread.QuestionMaxLength)
            .Must(text => !string.IsNullOrWhiteSpace(text)).WithMessage("The question cannot be blank.");
}

internal static class CopilotThreadSupport
{
    public const int PageSize = 30;

    public static ResourceNotFoundException NotFound(Guid threadId) =>
        new(AiErrorCodes.CopilotThreadNotFound, $"Copilot thread '{threadId}' was not found.");

    /// <summary>D-C8: sólo su dueño lo encuentra. Otro miembro del tenant recibe el mismo 404 que un id que no existe.</summary>
    public static async Task<CopilotThread> RequireOwnAsync(
        ICopilotRepository repository, IExecutionContext executionContext, Guid tenantId, Guid threadId, CancellationToken cancellationToken) =>
        await repository.FindThreadAsync(tenantId, executionContext.SubjectId, threadId, cancellationToken) ?? throw NotFound(threadId);

    public static ResourceConflictException Busy() =>
        new(AiErrorCodes.CopilotBusy, "Espera la respuesta del copiloto antes de preguntar otra cosa.");

    public static CopilotThreadDto ToDto(CopilotThread thread) =>
        new(
            thread.Id,
            thread.Title,
            [.. thread.Messages.OrderBy(message => message.Seq).Select(message => new CopilotMessageDto(
                message.Id,
                message.Role.ToString(),
                message.Text,
                message.Citations,
                message.Status.ToString(),
                message.Progress,
                message.ErrorCode,
                message.CreatedAt))],
            thread.HasPending,
            thread.Version);
}

/// <summary>
/// §7 paso 1 y D-C11: lo que se revisa antes de encolar una respuesta, en este orden: la configuración (422 <c>ai.copilot.disabled</c>), el
/// tope por miembro (429 <c>ai.copilot.rate_limited</c>, sólo al preguntar: reintentar no es otra pregunta), el tope mensual del tenant (429
/// <c>ai.copilot.budget_exhausted</c>) y la cuenta del proveedor elegido (422 <c>ai.provider.not_configured</c>, C-18; nunca cae al otro,
/// C-20). Scoped: lo comparten crear, enviar y reintentar.
/// </summary>
public sealed class CopilotAsking(
    ICopilotRepository repository,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    CopilotLimits limits,
    ITenantClock tenantClock)
{
    public async Task EnsureCanAskAsync(Guid tenantId, Guid userId, bool countsAsQuestion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await repository.FindSettingsAsync(tenantId, cancellationToken);
        if (settings is not { Enabled: true, Provider: { } provider, Model: { } model }
            || !catalog.Providers.Any(entry => entry.Provider.Value == provider && entry.Models.Any(candidate => candidate.Id == model && candidate.Available)))
        {
            throw new AiDomainException(AiErrorCodes.CopilotDisabled, "El copiloto no está configurado en tu organización.");
        }

        if (countsAsQuestion)
        {
            var questions = await repository.ListQuestionTimesAsync(tenantId, userId, now - CopilotLimits.Day, cancellationToken);
            if (limits.RetryAfter(questions, now) is { } wait)
            {
                throw CopilotRateLimitedException.MemberLimit(wait);
            }
        }

        if (settings.MonthlyTokenBudget is { } budget)
        {
            var (monthStart, nextMonth) = await CopilotSettingsSupport.MonthAsync(tenantClock, tenantId, cancellationToken);
            if ((await repository.SumTokensAsync(tenantId, monthStart, cancellationToken)).Total >= budget)
            {
                throw CopilotRateLimitedException.Budget(nextMonth - now);
            }
        }

        var chosen = AiProvider.Parse(provider);
        if ((await credentials.ResolveAsync(tenantId, chosen, cancellationToken)).Credential is null)
        {
            throw new AiProviderNotConfiguredException(chosen);
        }
    }
}

public sealed class ListCopilotThreadsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IValidator<ListCopilotThreadsQuery> validator, ICopilotRepository repository)
    : IQueryHandler<ListCopilotThreadsQuery, CopilotThreadPage>
{
    public async Task<CopilotThreadPage> HandleAsync(ListCopilotThreadsQuery query, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.CopilotUse, cancellationToken);
        CopilotThreadCursor? after = query.Cursor is not null && CopilotThreadCursor.TryDecode(query.Cursor, out var decoded) ? decoded : null;

        // Una de más para saber si hay otra página sin contar el total.
        var rows = await repository.ListThreadsAsync(
            query.TenantId, executionContext.SubjectId, after, CopilotThreadSupport.PageSize + 1, cancellationToken);
        var page = rows.Take(CopilotThreadSupport.PageSize).ToList();
        return new CopilotThreadPage(
            [.. page.Select(thread => new CopilotThreadSummaryDto(thread.Id, thread.Title, thread.LastActivityAt, thread.Version))],
            rows.Count > CopilotThreadSupport.PageSize ? CopilotThreadCursor.Encode(new CopilotThreadCursor(page[^1].LastActivityAt, page[^1].Id)) : null);
    }
}

public sealed class CreateCopilotThreadHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<CreateCopilotThreadCommand> validator,
    CopilotAsking asking,
    ICopilotRepository repository,
    IAiModelJobs jobs,
    IAiUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<CreateCopilotThreadCommand, CopilotThreadDto>
{
    public async Task<CopilotThreadDto> HandleAsync(CreateCopilotThreadCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var now = clock.UtcNow;
        await asking.EnsureCanAskAsync(command.TenantId, executionContext.SubjectId, countsAsQuestion: true, now, cancellationToken);

        // §7 paso 1: la pregunta, la respuesta pendiente y el trabajo en un solo guardado (una transacción).
        var jobId = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), command.TenantId, executionContext.SubjectId, command.Text, jobId, now);
        repository.AddThread(thread);
        jobs.Add(ModelJob.ForCopilot(jobId, command.TenantId, thread.Id, thread.Messages[^2].Seq, now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CopilotThreadSupport.ToDto(thread);
    }
}

public sealed class GetCopilotThreadHandler(IExecutionContext executionContext, ITenantModules tenantModules, ICopilotRepository repository)
    : IQueryHandler<GetCopilotThreadQuery, CopilotThreadDto>
{
    public async Task<CopilotThreadDto> HandleAsync(GetCopilotThreadQuery query, CancellationToken cancellationToken)
    {
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.CopilotUse, cancellationToken);
        return CopilotThreadSupport.ToDto(
            await CopilotThreadSupport.RequireOwnAsync(repository, executionContext, query.TenantId, query.ThreadId, cancellationToken));
    }
}

public sealed class SendCopilotMessageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<SendCopilotMessageCommand> validator,
    CopilotAsking asking,
    ICopilotRepository repository,
    IAiModelJobs jobs,
    IAiUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<SendCopilotMessageCommand, CopilotThreadDto>
{
    public async Task<CopilotThreadDto> HandleAsync(SendCopilotMessageCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var thread = await CopilotThreadSupport.RequireOwnAsync(repository, executionContext, command.TenantId, command.ThreadId, cancellationToken);
        if (thread.HasPending)
        {
            throw CopilotThreadSupport.Busy();
        }

        var now = clock.UtcNow;
        await asking.EnsureCanAskAsync(command.TenantId, executionContext.SubjectId, countsAsQuestion: true, now, cancellationToken);
        var jobId = Guid.CreateVersion7();
        thread.AppendUserMessage(command.Text, jobId, now);
        jobs.Add(ModelJob.ForCopilot(jobId, command.TenantId, thread.Id, thread.Messages[^2].Seq, now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CopilotThreadSupport.ToDto(thread);
    }
}

public sealed class RetryCopilotMessageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    CopilotAsking asking,
    ICopilotRepository repository,
    IAiModelJobs jobs,
    IAiUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<RetryCopilotMessageCommand, CopilotThreadDto>
{
    public async Task<CopilotThreadDto> HandleAsync(RetryCopilotMessageCommand command, CancellationToken cancellationToken)
    {
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var thread = await CopilotThreadSupport.RequireOwnAsync(repository, executionContext, command.TenantId, command.ThreadId, cancellationToken);
        if (thread.HasPending)
        {
            throw CopilotThreadSupport.Busy();
        }

        if (!thread.CanRetry(command.MessageId))
        {
            throw new ResourceConflictException(
                AiErrorCodes.CopilotNotRetryable, "Sólo se puede reintentar la última respuesta, si falló.");
        }

        var now = clock.UtcNow;
        await asking.EnsureCanAskAsync(command.TenantId, executionContext.SubjectId, countsAsQuestion: false, now, cancellationToken);
        var jobId = Guid.CreateVersion7();
        thread.RetryLast(command.MessageId, jobId, now);
        jobs.Add(ModelJob.ForCopilot(jobId, command.TenantId, thread.Id, thread.Messages[^2].Seq, now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CopilotThreadSupport.ToDto(thread);
    }
}

public sealed class RenameCopilotThreadHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<RenameCopilotThreadCommand> validator,
    ICopilotRepository repository,
    IAiUnitOfWork unitOfWork) : ICommandHandler<RenameCopilotThreadCommand, CopilotThreadDto>
{
    public async Task<CopilotThreadDto> HandleAsync(RenameCopilotThreadCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var thread = await CopilotThreadSupport.RequireOwnAsync(repository, executionContext, command.TenantId, command.ThreadId, cancellationToken);
        if (thread.Version != command.Version)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The copilot thread changed after it was loaded.");
        }

        thread.Rename(command.Title);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CopilotThreadSupport.ToDto(thread);
    }
}

/// <summary>D-C8: definitivo, sin papelera; se lleva los <c>tool_calls</c> de sus trabajos. Un trabajo en curso lo detecta en su commit y se
/// descarta (plan C2.3). D-C13 (plan C3.4): <c>ai.copilot.thread_deleted</c> con el miembro como actor, en la transacción del borrado.</summary>
public sealed class DeleteCopilotThreadHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ICopilotRepository repository, IAiAuditRecorder audit, IClock clock)
    : ICommandHandler<DeleteCopilotThreadCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteCopilotThreadCommand command, CancellationToken cancellationToken)
    {
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var thread = await CopilotThreadSupport.RequireOwnAsync(repository, executionContext, command.TenantId, command.ThreadId, cancellationToken);
        audit.RecordCopilot(
            command.TenantId, executionContext.SubjectId, AiAuditActions.CopilotThreadDeleted, AiAuditActions.ResourceCopilotThread, thread.Id,
            clock.UtcNow);
        await repository.DeleteThreadAsync(command.TenantId, thread.Id, cancellationToken);
        return true;
    }
}
