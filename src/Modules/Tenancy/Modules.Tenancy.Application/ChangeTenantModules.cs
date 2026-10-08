using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>Un cambio pedido, como llega en el JSON. Texto a propósito: se valida antes de convertir.</summary>
public sealed record TenantModuleChangeInput(string? Key, string? Status);

public sealed record ChangeTenantModulesCommand(
    TenantId TenantId,
    TenantId TargetTenantId,
    IReadOnlyList<TenantModuleChangeInput>? Changes,
    string? Reason,
    string? Note) : ICommand<OperatorTenantDetailDto>;

/// <summary>Texto libre ⇒ validador, aunque el dominio valide (CLAUDE.md): el dominio da el código, el
/// validador da el campo. Las reglas de negocio (D2, consistencia) son códigos de dominio.</summary>
public sealed class ChangeTenantModulesValidator : AbstractValidator<ChangeTenantModulesCommand>
{
    public ChangeTenantModulesValidator()
    {
        RuleFor(command => command.Changes)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(changes => changes!.Count <= TenantModuleKeys.All.Count)
            .WithMessage("A batch has at most seven changes.");
        // NotNull: un [null] del JSON llega como elemento null aunque el tipo diga que no; ChildRules lo
        // saltaría y el Parse del handler daría 500 (Review Focus 1).
        RuleForEach(command => command.Changes)
            .NotNull()
            .ChildRules(change =>
            {
                change.RuleFor(item => item.Key).Must(OperatorInput.IsModuleKey).WithMessage("Unknown module key.");
                change.RuleFor(item => item.Status).Must(OperatorInput.IsDirection).WithMessage("Status must be 'active' or 'inactive'.");
            });
        RuleFor(command => command.Reason).Must(OperatorInput.IsReason).WithMessage("Unknown reason.");
        RuleFor(command => command.Note).MaximumLength(TenantChange.NoteMaxLength);
    }
}

public sealed class ChangeTenantModulesHandler(
    ITenantRepository tenantRepository,
    ITenantModuleRepository moduleRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IOperatorTenantReader reader,
    IClock clock,
    IValidator<ChangeTenantModulesCommand> validator)
    : ICommandHandler<ChangeTenantModulesCommand, OperatorTenantDetailDto>
{
    public async Task<OperatorTenantDetailDto> HandleAsync(ChangeTenantModulesCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, OperatorPermissions.ModulesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // §3 (D3): sin If-Match; el candado serializa las operaciones sobre el mismo tenant y se toma
        // antes de leer nada, el tenant incluido.
        await using (var scope = await unitOfWork.BeginTenantChangeScopeAsync(command.TargetTenantId, cancellationToken))
        {
            _ = await tenantRepository.GetAsync(command.TargetTenantId, cancellationToken)
                ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

            var rows = await moduleRepository.ListByTenantAsync(command.TargetTenantId, cancellationToken);
            // El validador ya garantizó que el texto es conocido: estas conversiones no lanzan.
            var reason = TenantChangeVocabulary.ParseReason(command.Reason!);
            var requested = command.Changes!
                .Select(change => new RequestedModuleChange(
                    TenantModuleKey.Parse(change.Key!), TenantChangeVocabulary.ParseModuleStatus(change.Status!)))
                .ToArray();
            var plan = TenantModuleChangeBatch.Plan(rows.ToDictionary(row => row.ModuleKey, row => row.Status), requested, reason);

            var now = clock.UtcNow;
            var batchId = Guid.CreateVersion7();
            foreach (var change in plan)
            {
                var row = rows.SingleOrDefault(value => value.ModuleKey == change.Key);
                if (row is null)
                {
                    // §5: activar una clave sin fila la crea; la nota de creación queda vacía (decisión
                    // P8) porque el motivo y la nota viven en el historial.
                    moduleRepository.Add(TenantModule.Create(
                        command.TargetTenantId, change.Key, TenantModuleSources.Operator, now, note: null));
                }
                else if (change.To == TenantModuleStatus.Active)
                {
                    row.Activate(now);
                }
                else
                {
                    row.Deactivate(now);
                }

                changeRepository.Add(TenantChange.ForModule(
                    command.TargetTenantId, batchId, change.Key, change.From, change.To, reason, command.Note,
                    executionContext.SubjectId, now));
            }

            // §5: tenantId = el destino; un string por cambio más el motivo. Sin fila se anota "none",
            // el mismo texto que el detalle.
            auditRecorder.Record(
                command.TargetTenantId.Value,
                executionContext.SubjectId,
                "tenancy.tenant_modules.changed",
                "tenant",
                command.TargetTenantId.Value.ToString(),
                "success",
                plan.Select(change =>
                        $"{change.Key.Value}:{(change.From is { } from ? TenantChangeVocabulary.ToText(from) : OperatorTenantModuleDto.NoRowStatus)}->{TenantChangeVocabulary.ToText(change.To)}")
                    .Append($"reason:{TenantChangeVocabulary.ToText(reason)}")
                    .ToArray(),
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        // Después del commit: el detalle actualizado es la respuesta (la SPA reemplaza su caché con él).
        return await OperatorTenantDetails.LoadAsync(reader, operatorTenant, command.TargetTenantId, cancellationToken);
    }
}
