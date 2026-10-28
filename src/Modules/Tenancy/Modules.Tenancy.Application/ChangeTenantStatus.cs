using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="Status"><c>inactive</c> inactiva (Suspend), <c>active</c> reactiva (decisión P12 del plan).</param>
/// <param name="ExpectedVersion">El If-Match: el tenant ya tiene versión de agregado (D3).</param>
public sealed record ChangeTenantStatusCommand(
    TenantId TenantId,
    TenantId TargetTenantId,
    string? Status,
    string? Reason,
    string? Note,
    long ExpectedVersion) : ICommand<OperatorTenantDetailDto>;

/// <summary>Texto libre ⇒ validador (CLAUDE.md): el validador da el campo y el dominio el código. Los
/// motivos por dirección (D2) son del dominio.</summary>
public sealed class ChangeTenantStatusValidator : AbstractValidator<ChangeTenantStatusCommand>
{
    public ChangeTenantStatusValidator()
    {
        RuleFor(command => command.Status).Must(OperatorInput.IsDirection).WithMessage("Status must be 'active' or 'inactive'.");
        RuleFor(command => command.Reason).Must(OperatorInput.IsReason).WithMessage("Unknown reason.");
        RuleFor(command => command.Note).MaximumLength(TenantChange.NoteMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class ChangeTenantStatusHandler(
    ITenantRepository tenantRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IOperatorTenantReader reader,
    IClock clock,
    IValidator<ChangeTenantStatusCommand> validator)
    : ICommandHandler<ChangeTenantStatusCommand, OperatorTenantDetailDto>
{
    public async Task<OperatorTenantDetailDto> HandleAsync(ChangeTenantStatusCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, OperatorPermissions.TenantsManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // El validador ya garantizó que el texto es conocido: estas conversiones no lanzan.
        var suspend = TenantChangeVocabulary.ParseModuleStatus(command.Status!) == TenantModuleStatus.Inactive;
        // §4: antes de tocar el agregado; el dominio no conoce la configuración.
        if (suspend && operatorTenant.IsOperator(command.TargetTenantId.Value))
        {
            throw new TenantDomainException(
                "tenancy.tenant.operator_cannot_be_suspended",
                "The operator tenant cannot be suspended.");
        }

        var tenant = await tenantRepository.GetAsync(command.TargetTenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");
        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The tenant changed after it was loaded.");
        }

        var reason = TenantChangeVocabulary.ParseReason(command.Reason!);
        var now = clock.UtcNow;
        var from = tenant.Status;
        if (suspend)
        {
            tenant.Suspend(reason, now);
        }
        else
        {
            tenant.Reactivate(reason, now);
        }

        changeRepository.Add(TenantChange.ForTenantStatus(
            tenant.Id, Guid.CreateVersion7(), from, tenant.Status, reason, command.Note, executionContext.SubjectId, now));
        // §5: misma forma que la auditoría de módulos (decisión P10 del plan).
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.tenant.status_changed",
            "tenant",
            tenant.Id.Value.ToString(),
            "success",
            [$"status:{from}->{tenant.Status}", $"reason:{TenantChangeVocabulary.ToText(reason)}"],
            now);
        // Version es token de concurrencia: un choque entre la lectura y el SaveChanges sale 412 por
        // TenancyUnitOfWork (concurrency.conflict).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OperatorTenantDetails.LoadAsync(reader, operatorTenant, tenant.Id, cancellationToken);
    }
}
