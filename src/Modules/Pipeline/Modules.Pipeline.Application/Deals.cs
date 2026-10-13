using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

public sealed record GetDealQuery(Guid TenantId, Guid DealId) : IQuery<DealDetailDto>;

public sealed record CreateDealCommand(
    Guid TenantId, Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId)
    : ICommand<DealDetailDto>;

public sealed record UpdateDealCommand(
    Guid TenantId, Guid DealId, string? Title, Guid? OwnerMemberId, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, long? Version)
    : ICommand<DealDetailDto>;

public sealed record MoveDealCommand(Guid TenantId, Guid DealId, Guid? StageId, long? Version) : ICommand<DealDetailDto>;

public sealed record WinDealCommand(Guid TenantId, Guid DealId, long? Version) : ICommand<DealDetailDto>;

public sealed record LoseDealCommand(Guid TenantId, Guid DealId, Guid? LossReasonId, string? Note, long? Version) : ICommand<DealDetailDto>;

public sealed record ReopenDealCommand(Guid TenantId, Guid DealId, long? Version) : ICommand<DealDetailDto>;

public sealed record DealCommandContext(SalesPipeline Pipeline, Deal Deal, DealActor Actor, DateTimeOffset Now);

/// <summary>Texto libre ⇒ validador (CLAUDE.md): el validador da el campo; el dominio, el código.</summary>
public sealed class CreateDealValidator : AbstractValidator<CreateDealCommand>
{
    public CreateDealValidator()
    {
        RuleFor(command => command.CustomerId).NotEmpty();
        RuleFor(command => command.Title).NotEmpty().MaximumLength(Deal.TitleMaxLength);
        RuleFor(command => command.Amount).GreaterThanOrEqualTo(0m).When(command => command.Amount is not null);
        RuleFor(command => command.Currency).Must(Currencies.IsSupported).When(command => command.Currency is not null)
            .WithMessage("Unsupported currency.");
    }
}

public sealed class UpdateDealValidator : AbstractValidator<UpdateDealCommand>
{
    public UpdateDealValidator()
    {
        RuleFor(command => command.Title).NotEmpty().MaximumLength(Deal.TitleMaxLength);
        RuleFor(command => command.OwnerMemberId).NotEmpty();
        RuleFor(command => command.Amount).GreaterThanOrEqualTo(0m).When(command => command.Amount is not null);
        RuleFor(command => command.Currency).NotEmpty().Must(Currencies.IsSupported).WithMessage("Unsupported currency.");
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class MoveDealValidator : AbstractValidator<MoveDealCommand>
{
    public MoveDealValidator()
    {
        RuleFor(command => command.StageId).NotEmpty();
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class WinDealValidator : AbstractValidator<WinDealCommand>
{
    public WinDealValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>P10 del plan: sin motivo NO es <c>validation.failed</c>; el dominio responde <c>pipeline.deal.loss_reason_required</c>.</summary>
public sealed class LoseDealValidator : AbstractValidator<LoseDealCommand>
{
    public LoseDealValidator()
    {
        RuleFor(command => command.Note).MaximumLength(Deal.LossNoteMaxLength);
        RuleFor(command => command.Version).NotNull();
    }
}

public sealed class ReopenDealValidator : AbstractValidator<ReopenDealCommand>
{
    public ReopenDealValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>
/// El esqueleto común de las escrituras sobre un negocio existente: autorizar (antes de validar, como el resto del repo), cargar
/// con la versión que la pantalla leyó (412 si cambió), resolver el actor y, al final, auditar, guardar (con el historial, P4) y
/// devolver el detalle entero. Público sólo para que Infrastructure lo registre.
/// </summary>
public sealed class DealCommandSteps(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    PipelineCaller caller,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealDetailBuilder detail,
    IClock clock)
{
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        PipelineAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, PipelinePermissions.DealManage, cancellationToken);

    public async Task<DealCommandContext> LoadAsync(Guid tenantId, Guid dealId, long? version, CancellationToken cancellationToken)
    {
        var pipeline = await provisioner.EnsureAsync(tenantId, cancellationToken);
        var deal = await deals.FindAsync(tenantId, dealId, cancellationToken) ?? throw PipelineNotFound.Deal(dealId);
        PipelineVersions.Ensure(deal.Version, version);
        var memberId = await caller.RequireAsync(tenantId, cancellationToken);
        return new DealCommandContext(pipeline, deal, DealActor.ForUser(memberId), clock.UtcNow);
    }

    /// <summary>Una entrada de auditoría más, sobre este negocio u otro que la misma escritura tocó (enlazar a mano audita el
    /// negocio que pierde la cotización, Task 10). Va en el SaveChanges de <see cref="CommitAsync"/>.</summary>
    public void RecordAudit(DealCommandContext context, string action, Guid dealId)
    {
        ArgumentNullException.ThrowIfNull(context);
        audit.Record(context.Deal.TenantId, executionContext.SubjectId, false, action, PipelineAuditActions.DealResource, dealId, context.Now);
    }

    /// <summary>Sin <paramref name="auditAction"/> no hubo cambio (mover a la misma etapa): no se audita ni se guarda.</summary>
    public async Task<DealDetailDto> CommitAsync(DealCommandContext context, string? auditAction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (auditAction is not null)
        {
            RecordAudit(context, auditAction, context.Deal.Id);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await detail.BuildAsync(context.Pipeline, context.Deal, cancellationToken);
    }
}

public sealed class GetDealHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IPipelineProvisioner provisioner, IDealRepository deals, DealDetailBuilder detail)
    : IQueryHandler<GetDealQuery, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(GetDealQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var deal = await deals.FindAsync(query.TenantId, query.DealId, cancellationToken) ?? throw PipelineNotFound.Deal(query.DealId);
        return await detail.BuildAsync(pipeline, deal, cancellationToken);
    }
}

/// <summary>Lo que hace falta para crear un negocio, venga de la API o del triage. <see cref="Currency"/> nula = la moneda por
/// defecto del tenant (D34: el negocio puede nacer sin monto, pero siempre con moneda).</summary>
public sealed record DealDraft(
    Guid TenantId, Guid CustomerId, Guid OwnerMemberId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate,
    DealSource Source, Guid? OriginConversationId);

/// <summary>
/// §4.2 y D29: el único camino para crear un negocio desde la API —<see cref="CreateDealHandler"/> y el <c>create</c> del
/// triage—: embudo en el primer acceso, moneda, <see cref="Deal.Create"/>, auditoría <c>pipeline.deal.created</c>, un solo
/// SaveChanges (con el historial, P4) y el detalle entero. <paramref name="beforeSave"/> deja al llamador sumar lo suyo a esa
/// misma unidad de trabajo con el negocio ya creado: el triage agrega su decisión, así la carrera de D38 revierte las dos cosas
/// juntas. Lo que cada llamador valida antes (cliente, dueño, conversación pendiente) queda en el llamador. Público sólo para que
/// Infrastructure lo registre.
/// </summary>
public sealed class DealCreator(
    IExecutionContext executionContext,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    ITenantDefaultCurrency defaultCurrency,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealDetailBuilder detail)
{
    public async Task<DealDetailDto> CreateAsync(
        DealDraft draft, DealActor actor, DateTimeOffset now, Action<Deal>? beforeSave, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var currency = draft.Currency is null
            ? await defaultCurrency.GetAsync(draft.TenantId, cancellationToken)
            : Currencies.Normalize(draft.Currency);
        var pipeline = await provisioner.EnsureAsync(draft.TenantId, cancellationToken);

        var deal = Deal.Create(draft.TenantId, pipeline, draft.CustomerId, draft.OwnerMemberId, draft.Title, draft.Amount, currency,
            draft.ExpectedCloseDate, draft.Source, draft.OriginConversationId, actor, now);
        deals.Add(deal);
        audit.Record(draft.TenantId, executionContext.SubjectId, false, PipelineAuditActions.DealCreated, PipelineAuditActions.DealResource, deal.Id, now);
        beforeSave?.Invoke(deal);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await detail.BuildAsync(pipeline, deal, cancellationToken);
    }
}

/// <summary>§4.2: un negocio manual (D34: puede nacer sin monto; la moneda por defecto es la del tenant). El dueño por defecto es
/// quien llama; otro dueño tiene que ser miembro del tenant. Crear lo hace <see cref="DealCreator"/>.</summary>
public sealed class CreateDealHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    PipelineCaller caller,
    DealCreator creator,
    IClock clock,
    IValidator<CreateDealCommand> validator)
    : ICommandHandler<CreateDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(CreateDealCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.DealManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customerId = command.CustomerId!.Value;
        if (!(await customers.FindAsync(command.TenantId, [customerId], cancellationToken)).ContainsKey(customerId))
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealCustomerNotFound, "The customer was not found in this tenant.");
        }

        var callerId = await caller.RequireAsync(command.TenantId, cancellationToken);
        var owner = command.OwnerMemberId ?? callerId;
        await DealOwners.EnsureMemberAsync(members, command.TenantId, owner, callerId, cancellationToken);

        return await creator.CreateAsync(
            new DealDraft(command.TenantId, customerId, owner, command.Title, command.Amount, command.Currency, command.ExpectedCloseDate,
                DealSource.Manual, null),
            DealActor.ForUser(callerId), clock.UtcNow, beforeSave: null, cancellationToken);
    }
}

internal static class DealOwners
{
    /// <summary>El dueño tiene que ser una membresía del tenant. Quien llama ya lo es (lo resolvió <c>PipelineCaller</c>).</summary>
    public static async Task EnsureMemberAsync(
        IPipelineMemberNames members, Guid tenantId, Guid owner, Guid callerId, CancellationToken cancellationToken)
    {
        if (owner != callerId && !(await members.FindAsync(tenantId, [owner], cancellationToken)).ContainsKey(owner))
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealOwnerNotMember, "The owner is not a member of this tenant.");
        }
    }
}

/// <summary>P5 del plan: reemplaza los editables. Con principal (D18), monto y moneda no cambian (lo dice el dominio).</summary>
public sealed class UpdateDealHandler(
    DealCommandSteps steps, IPipelineMemberNames members, PipelineCaller caller, IValidator<UpdateDealCommand> validator)
    : ICommandHandler<UpdateDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(UpdateDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var callerId = await caller.RequireAsync(command.TenantId, cancellationToken);
        await DealOwners.EnsureMemberAsync(members, command.TenantId, command.OwnerMemberId!.Value, callerId, cancellationToken);

        context.Deal.Update(command.Title, command.OwnerMemberId.Value, command.Amount, Currencies.Normalize(command.Currency),
            command.ExpectedCloseDate, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealUpdated, cancellationToken);
    }
}

/// <summary>D13: libre entre abiertas. A la misma etapa no hay cambio, ni auditoría.</summary>
public sealed class MoveDealHandler(DealCommandSteps steps, IValidator<MoveDealCommand> validator)
    : ICommandHandler<MoveDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(MoveDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var moved = context.Deal.MoveTo(context.Pipeline, command.StageId!.Value, context.Actor, context.Now);
        return await steps.CommitAsync(context, moved ? PipelineAuditActions.DealMoved : null, cancellationToken);
    }
}

/// <summary>D14: ganar a mano se permite.</summary>
public sealed class WinDealHandler(DealCommandSteps steps, IValidator<WinDealCommand> validator)
    : ICommandHandler<WinDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(WinDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        context.Deal.Win(context.Pipeline, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealWon, cancellationToken);
    }
}

/// <summary>D14: el motivo tiene que ser un motivo de pérdida activo del tenant.</summary>
public sealed class LoseDealHandler(DealCommandSteps steps, IPipelineReasonRepository reasons, IValidator<LoseDealCommand> validator)
    : ICommandHandler<LoseDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(LoseDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        if (command.LossReasonId is { } reasonId && reasonId != Guid.Empty &&
            await reasons.FindActiveAsync(command.TenantId, ReasonKind.Loss, reasonId, cancellationToken) is null)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossReasonNotFound, "The loss reason is not an active reason of this tenant.");
        }

        context.Deal.Lose(context.Pipeline, command.LossReasonId, command.Note, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealLost, cancellationToken);
    }
}

/// <summary>D15: a la última etapa abierta del historial.</summary>
public sealed class ReopenDealHandler(DealCommandSteps steps, IDealRepository deals, IValidator<ReopenDealCommand> validator)
    : ICommandHandler<ReopenDealCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(ReopenDealCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var lastOpen = await deals.FindLastOpenStageIdAsync(command.TenantId, command.DealId, cancellationToken);
        context.Deal.Reopen(context.Pipeline, lastOpen, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealReopened, cancellationToken);
    }
}
