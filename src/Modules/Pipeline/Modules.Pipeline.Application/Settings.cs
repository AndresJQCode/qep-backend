using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

public sealed record GetPipelineStagesQuery(Guid TenantId) : IQuery<PipelineStagesDto>;

public sealed record ReplacePipelineStagesCommand(Guid TenantId, long? Version, IReadOnlyList<StageRequest>? Stages) : ICommand<PipelineStagesDto>;

public sealed record GetPipelineReasonsQuery(Guid TenantId, ReasonKind Kind) : IQuery<ReasonListDto>;

public sealed record ReplacePipelineReasonsCommand(Guid TenantId, ReasonKind Kind, IReadOnlyList<ReasonRequest>? Items) : ICommand<ReasonListDto>;

/// <summary>Texto libre ⇒ validador, aunque el dominio valide (CLAUDE.md): el validador da el campo, el dominio el código
/// (<c>pipeline.stage.layout_invalid</c>, <c>has_deals</c>).</summary>
public sealed class ReplacePipelineStagesValidator : AbstractValidator<ReplacePipelineStagesCommand>
{
    public ReplacePipelineStagesValidator()
    {
        RuleFor(command => command.Version).NotNull();
        RuleFor(command => command.Stages).NotEmpty();
        RuleForEach(command => command.Stages)
            .NotNull()
            .ChildRules(stage =>
            {
                stage.RuleFor(item => item.Name).NotEmpty().MaximumLength(SalesPipeline.StageNameMaxLength);
                stage.RuleFor(item => item.Kind)
                    .Must(kind => Enum.TryParse<StageKind>(kind, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
                    .WithMessage("Kind must be Open, Won or Lost.");
                stage.RuleFor(item => item.Probability).NotNull().InclusiveBetween(0, 100);
            });
    }
}

public sealed class ReplacePipelineReasonsValidator : AbstractValidator<ReplacePipelineReasonsCommand>
{
    public ReplacePipelineReasonsValidator()
    {
        RuleFor(command => command.Items).NotEmpty();
        RuleForEach(command => command.Items)
            .NotNull()
            .ChildRules(item => item.RuleFor(reason => reason.Name).NotEmpty().MaximumLength(PipelineReason.NameMaxLength));
    }
}

public sealed class GetPipelineStagesHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner)
    : IQueryHandler<GetPipelineStagesQuery, PipelineStagesDto>
{
    public async Task<PipelineStagesDto> HandleAsync(GetPipelineStagesQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        return PipelineMapping.ToDto(await provisioner.EnsureAsync(query.TenantId, cancellationToken));
    }
}

/// <summary>D11 y D12: la colección entera, con la versión del embudo. Las etapas con negocios no se borran.</summary>
public sealed class ReplacePipelineStagesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    IClock clock,
    IValidator<ReplacePipelineStagesCommand> validator)
    : ICommandHandler<ReplacePipelineStagesCommand, PipelineStagesDto>
{
    public async Task<PipelineStagesDto> HandleAsync(ReplacePipelineStagesCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        PipelineVersions.Ensure(pipeline.Version, command.Version);
        // El validador ya garantizó nombre, tipo y probabilidad: estas conversiones no lanzan.
        var inputs = command.Stages!
            .Select(stage => new StageInput(stage.Id, stage.Name!, Enum.Parse<StageKind>(stage.Kind!), stage.Probability!.Value, stage.StallDays, stage.IsQuotationStage))
            .ToArray();
        var stagesWithDeals = await deals.StagesWithDealsAsync(command.TenantId, pipeline.Id, cancellationToken);
        var now = clock.UtcNow;

        pipeline.ReplaceStages(inputs, stagesWithDeals, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.StagesReplaced, PipelineAuditActions.SettingsResource, pipeline.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PipelineMapping.ToDto(pipeline);
    }
}

public sealed class GetPipelineReasonsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner, IPipelineReasonRepository reasons)
    : IQueryHandler<GetPipelineReasonsQuery, ReasonListDto>
{
    public async Task<ReasonListDto> HandleAsync(GetPipelineReasonsQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        // D4: la semilla de los catálogos viaja con el embudo.
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        return PipelineMapping.ToDto(query.Kind, await reasons.ListAsync(query.TenantId, query.Kind, cancellationToken));
    }
}

/// <summary>P8 del plan: reemplazo entero, sin versión; lo que deja de venir se desactiva (el historial lo nombra).</summary>
public sealed class ReplacePipelineReasonsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IPipelineReasonRepository reasons,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    IClock clock,
    IValidator<ReplacePipelineReasonsCommand> validator)
    : ICommandHandler<ReplacePipelineReasonsCommand, ReasonListDto>
{
    public async Task<ReasonListDto> HandleAsync(ReplacePipelineReasonsCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var pipeline = await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var current = await reasons.ListAsync(command.TenantId, command.Kind, cancellationToken);
        var now = clock.UtcNow;
        var added = ReasonCatalog.Replace(
            command.TenantId, command.Kind, current, [.. command.Items!.Select(item => new ReasonInput(item.Id, item.Name!))], now);
        foreach (var reason in added)
        {
            reasons.Add(reason);
        }

        var action = command.Kind == ReasonKind.Loss ? PipelineAuditActions.LossReasonsReplaced : PipelineAuditActions.DismissReasonsReplaced;
        audit.Record(command.TenantId, executionContext.SubjectId, false, action, PipelineAuditActions.SettingsResource, pipeline.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PipelineMapping.ToDto(command.Kind, current.Concat(added));
    }
}
