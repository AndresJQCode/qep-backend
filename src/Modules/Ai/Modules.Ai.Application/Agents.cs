using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

// Spec 2026-10-29 §5: lista, creación (en blanco o como duplicado), lectura, edición, borrador, publicación, pausa, reanudación y borrado del
// agente, y las conexiones elegibles. Leer con ai.agent.read; escribir con ai.agent.manage, con If-Match sobre un agente existente.

public sealed record ListAgentsQuery(Guid TenantId, string? Search) : IQuery<AgentListResponse>;

public sealed record GetAgentQuery(Guid TenantId, Guid AgentId) : IQuery<AgentEditorDto>;

public sealed record ListAiConnectionsQuery(Guid TenantId) : IQuery<IReadOnlyList<AiConnectionDto>>;

/// <summary><see cref="Kind"/> es <c>blank</c> o <c>duplicate</c> (con <see cref="AgentId"/>): el duplicado copia el borrador, nunca las
/// conexiones, que al publicarse chocarían con las del original.</summary>
public sealed record AgentSource(string Kind, Guid? AgentId)
{
    public const string Blank = "blank";
    public const string Duplicate = "duplicate";
}

public sealed record CreateAgentCommand(Guid TenantId, string Name, AgentSource Source) : ICommand<AgentEditorDto>;

public sealed record UpdateAgentCommand(Guid TenantId, Guid AgentId, long Version, string Name, IReadOnlyList<Guid> ConnectionIds)
    : ICommand<AgentEditorDto>;

public sealed record SaveAgentDraftCommand(Guid TenantId, Guid AgentId, long Version, AgentConfig Config) : ICommand<AgentDraftSavedDto>;

public sealed record PublishAgentCommand(Guid TenantId, Guid AgentId, long Version) : ICommand<AgentEditorDto>;

public sealed record PauseAgentCommand(Guid TenantId, Guid AgentId, long Version) : ICommand<AgentEditorDto>;

public sealed record ResumeAgentCommand(Guid TenantId, Guid AgentId, long Version) : ICommand<AgentEditorDto>;

public sealed record DeleteAgentCommand(Guid TenantId, Guid AgentId, long Version) : ICommand<bool>;

public static class AgentLimits
{
    /// <summary>Como los chatbots: cuántas conexiones puede atender un agente.</summary>
    public const int MaxConnections = 10;

    /// <summary>§4.7: <c>draft_saved</c> se audita como máximo una vez por minuto, como Automations.</summary>
    public static readonly TimeSpan DraftAuditInterval = TimeSpan.FromMinutes(1);
}

internal static class AgentRules
{
    public static void Name<T>(IRuleBuilderInitial<T, string> rule) => rule.NotEmpty().MaximumLength(Agent.NameMaxLength);
}

public sealed class CreateAgentValidator : AbstractValidator<CreateAgentCommand>
{
    public CreateAgentValidator()
    {
        AgentRules.Name(RuleFor(command => command.Name));
        RuleFor(command => command.Source).NotNull();
        When(command => command.Source is not null, () =>
        {
            RuleFor(command => command.Source.Kind)
                .Must(kind => kind is AgentSource.Blank or AgentSource.Duplicate)
                .WithMessage("Source kind must be blank or duplicate.");
            RuleFor(command => command.Source.AgentId).NotNull().When(command => command.Source.Kind == AgentSource.Duplicate);
        });
    }
}

public sealed class UpdateAgentValidator : AbstractValidator<UpdateAgentCommand>
{
    public UpdateAgentValidator()
    {
        AgentRules.Name(RuleFor(command => command.Name));
        RuleFor(command => command.ConnectionIds)
            .NotNull()
            .Must(ids => ids.Count <= AgentLimits.MaxConnections)
            .WithMessage($"At most {AgentLimits.MaxConnections} connections are allowed.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("A connection cannot be repeated.");
    }
}

/// <summary>§4.6: los topes y los valores cerrados del <c>config</c>. Fuera de ellos es 422 <c>validation.failed</c> con el campo y no se
/// guarda; lo que falta para publicar (instrucciones vacías, un miembro que ya no responde) se guarda y vuelve como problema (D-A7).</summary>
public sealed class SaveAgentDraftValidator : AbstractValidator<SaveAgentDraftCommand>
{
    public SaveAgentDraftValidator()
    {
        RuleFor(command => command.Config).NotNull();
        When(command => command.Config is not null, () =>
        {
            RuleFor(command => command.Config.Instructions).NotNull().MaximumLength(AgentConfig.InstructionsMaxLength);
            RuleFor(command => command.Config.Tone).Must(tone => AgentConfig.Tones.Contains(tone))
                .WithMessage($"Tone must be one of {string.Join(", ", AgentConfig.Tones)}.");
            RuleFor(command => command.Config.Greeting).MaximumLength(AgentConfig.GreetingMaxLength);
            RuleFor(command => command.Config.Trigger).Must(trigger => AgentConfig.Triggers.Contains(trigger))
                .WithMessage($"Trigger must be one of {string.Join(", ", AgentConfig.Triggers)}.");
            RuleFor(command => command.Config.Faq)
                .NotNull()
                .Must(faq => faq.Count <= AgentConfig.MaxFaq)
                .WithMessage($"At most {AgentConfig.MaxFaq} questions are allowed.")
                .Must(faq => faq.Where(entry => entry is not null).Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == faq.Count)
                .WithMessage("Each question needs its own id.");
            RuleForEach(command => command.Config.Faq).ChildRules(entry =>
            {
                entry.RuleFor(value => value.Id).NotEmpty().MaximumLength(AgentConfig.FaqIdMaxLength);
                entry.RuleFor(value => value.Question).NotEmpty().MaximumLength(AgentConfig.FaqQuestionMaxLength);
                entry.RuleFor(value => value.Answer).NotEmpty().MaximumLength(AgentConfig.FaqAnswerMaxLength);
            }).When(command => command.Config.Faq is not null);
            RuleFor(command => command.Config.Tools).NotNull();
            RuleFor(command => command.Config.Handoff).NotNull();
            When(command => command.Config.Handoff is not null, () =>
            {
                RuleFor(command => command.Config.Handoff.Destination).Must(destination => AgentHandoff.Destinations.Contains(destination))
                    .WithMessage("Destination must be unassigned or member.");
                RuleFor(command => command.Config.Handoff.Message).MaximumLength(AgentHandoff.MessageMaxLength);
                RuleFor(command => command.Config.Handoff.MaxRepliesWithoutHuman)
                    .InclusiveBetween(AgentHandoff.MinRepliesWithoutHuman, AgentHandoff.MaxRepliesWithoutHumanLimit);
                RuleFor(command => command.Config.Handoff.MaxConsecutiveFailures)
                    .InclusiveBetween(AgentHandoff.MinConsecutiveFailures, AgentHandoff.MaxConsecutiveFailuresLimit);
            });
            RuleFor(command => command.Config.Model).NotNull();
            When(command => command.Config.Model is not null, () =>
            {
                RuleFor(command => command.Config.Model.Provider).NotEmpty().MaximumLength(AgentModelRef.ProviderMaxLength);
                RuleFor(command => command.Config.Model.Id).NotEmpty().MaximumLength(AgentModelRef.IdMaxLength);
            });
        });
    }
}

public sealed class ListAgentsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentRepository agents, AgentEditorBuilder builder)
    : IQueryHandler<ListAgentsQuery, AgentListResponse>
{
    public async Task<AgentListResponse> HandleAsync(ListAgentsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        return await builder.BuildListAsync(query.TenantId, await agents.ListAsync(query.TenantId, query.Search, cancellationToken), cancellationToken);
    }
}

public sealed class GetAgentHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentRepository agents, AgentEditorBuilder builder)
    : IQueryHandler<GetAgentQuery, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(GetAgentQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        var agent = await agents.FindAsync(query.TenantId, query.AgentId, cancellationToken) ?? throw AiNotFound.Agent(query.AgentId);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}

public sealed class ListAiConnectionsHandler(IExecutionContext executionContext, ITenantModules tenantModules, AgentEditorBuilder builder)
    : IQueryHandler<ListAiConnectionsQuery, IReadOnlyList<AiConnectionDto>>
{
    public async Task<IReadOnlyList<AiConnectionDto>> HandleAsync(ListAiConnectionsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        return await builder.ListSelectableAsync(query.TenantId, cancellationToken);
    }
}

/// <summary>Nace en borrador (D-A5) sin conexiones. En blanco toma el modelo predeterminado del proveedor predeterminado
/// (<c>Ai:DefaultProvider</c>, plan 2026-11-12 C-3); un duplicado copia el borrador del original, con su modelo.</summary>
public sealed class CreateAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiModelCatalog catalog,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    IValidator<CreateAgentCommand> validator,
    AgentEditorBuilder builder)
    : ICommandHandler<CreateAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(CreateAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var config = command.Source.Kind == AgentSource.Duplicate
            ? ((await agents.FindAsync(command.TenantId, command.Source.AgentId!.Value, cancellationToken))
                ?? throw AiNotFound.Agent(command.Source.AgentId.Value)).DraftConfig
            : AgentConfig.Default(ModelCatalog.DefaultFor(catalog));
        await EnsureNameIsFreeAsync(agents, command.TenantId, command.Name, null, cancellationToken);

        var now = clock.UtcNow;
        var agent = Agent.Create(command.TenantId, command.Name, config, by, now);
        agents.Add(agent);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentCreated, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }

    /// <summary>El índice único (<c>IX_agents_tenant_name</c>) es el respaldo ante dos creaciones simultáneas; esto da el 409 sin llegar a él.</summary>
    internal static async Task EnsureNameIsFreeAsync(
        IAgentRepository agents, Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await agents.NameExistsAsync(tenantId, name.Trim(), exceptId, cancellationToken))
        {
            throw new ResourceConflictException(AiErrorCodes.AgentNameTaken, "Ya existe un agente con ese nombre.");
        }
    }
}

/// <summary>Nombre y conexiones (§5). En un agente publicado las conexiones nuevas quedan ocupadas en el mismo commit y las quitadas,
/// liberadas; una que ocupa otro bot es 409 con el ocupante.</summary>
public sealed class UpdateAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    IValidator<UpdateAgentCommand> validator,
    AgentEditorBuilder builder)
    : ICommandHandler<UpdateAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(UpdateAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        await builder.EnsureConnectionsAsync(command.TenantId, command.ConnectionIds, cancellationToken);
        await CreateAgentHandler.EnsureNameIsFreeAsync(agents, command.TenantId, command.Name, agent.Id, cancellationToken);

        var now = clock.UtcNow;
        agent.Rename(command.Name, now);
        agent.SetConnections(command.ConnectionIds, now);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentUpdated, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}

/// <summary>D-A7: guarda siempre que el documento respete sus topes, aunque le falte algo para publicar, y responde los problemas.</summary>
public sealed class SaveAgentDraftHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    IValidator<SaveAgentDraftCommand> validator,
    AgentEditorBuilder builder)
    : ICommandHandler<SaveAgentDraftCommand, AgentDraftSavedDto>
{
    public async Task<AgentDraftSavedDto> HandleAsync(SaveAgentDraftCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        // El editor guarda mientras se escribe: una entrada de auditoría por minuto como mucho, medido contra el guardado anterior.
        var audited = now - agent.DraftUpdatedAt >= AgentLimits.DraftAuditInterval;
        agent.SaveDraft(Normalize(command.Config), by, now);
        if (audited)
        {
            audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentDraftSaved, agent.Id, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var issues = await builder.ValidateAsync(agent, publishing: false, cancellationToken);
        return new AgentDraftSavedDto(agent.Version, agent.DraftUpdatedAt, [.. issues.Select(AgentEditorBuilder.ToDto)]);
    }

    /// <summary>Un saludo o un mensaje de traspaso vacío es lo mismo que no tenerlo; el miembro sólo significa algo con destino
    /// <c>member</c>, y se descarta en los demás para que el documento no guarde un destino que nadie usa.</summary>
    private static AgentConfig Normalize(AgentConfig config) => config with
    {
        Greeting = string.IsNullOrWhiteSpace(config.Greeting) ? null : config.Greeting,
        Handoff = config.Handoff with
        {
            MemberId = config.Handoff.Destination == AgentHandoff.ToMember ? config.Handoff.MemberId : null,
            Message = string.IsNullOrWhiteSpace(config.Handoff.Message) ? null : config.Handoff.Message,
        },
    };
}

/// <summary>
/// D-A5 y plan 2026-11-12 C-16..C-18: copia el borrador a una versión con su proveedor y su modelo. Primero los problemas del borrador
/// (422 <c>ai.agent.invalid</c>), después la cuenta del proveedor <b>del agente</b> (422 <c>ai.provider.not_configured</c> con
/// <c>provider</c>): una conexión de otro proveedor no sirve. Ocupar las conexiones lo hace la unidad de trabajo en el mismo commit.
/// </summary>
public sealed class PublishAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiProviderCredentials credentials,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    AgentEditorBuilder builder)
    : ICommandHandler<PublishAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(PublishAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var issues = await builder.ValidateAsync(agent, publishing: true, cancellationToken);
        if (issues.Any(issue => issue.Severity == AgentIssueSeverity.Error))
        {
            agent.Publish(issues, by, clock.UtcNow);
        }

        // Sin errores, el proveedor existe: el validador marca uno desconocido.
        var provider = AiProvider.Parse(agent.DraftConfig.Model.Provider);
        if ((await credentials.ResolveAsync(command.TenantId, provider, cancellationToken)).Credential is null)
        {
            throw new AiProviderNotConfiguredException(provider);
        }

        var now = clock.UtcNow;
        agent.Publish(issues, by, now);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentPublished, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}

public sealed class PauseAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    AgentEditorBuilder builder)
    : ICommandHandler<PauseAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(PauseAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        var now = clock.UtcNow;
        agent.Pause(now);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentPaused, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}

public sealed class ResumeAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    AgentEditorBuilder builder)
    : ICommandHandler<ResumeAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(ResumeAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        var now = clock.UtcNow;
        agent.Resume(now);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentResumed, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}

/// <summary>Borra el agente con sus versiones y conexiones, y libera la ocupación en el mismo commit. El 409 <c>has_live_sessions</c> del
/// spec llega con las sesiones (plan 2026-11-12, Tarea 7): hoy no hay sesiones que cuidar.</summary>
public sealed class DeleteAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock)
    : ICommandHandler<DeleteAgentCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        var agent = await agents.FindAsync(command.TenantId, command.AgentId, cancellationToken) ?? throw AiNotFound.Agent(command.AgentId);
        AiVersions.Ensure(agent.Version, command.Version);
        agents.Remove(agent);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentDeleted, agent.Id, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
