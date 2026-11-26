using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record DeactivateEmployeeCommand(Guid TenantId, Guid EmployeeId, DeactivateRequest Request) : ICommand<EmployeeDetailDto>;

/// <summary>
/// Desactivar cierra el contrato vigente el día pedido (estado <c>Closed</c>, el empleado queda <c>Deactivated</c>). Una fecha anterior
/// al inicio es 422 sobre <c>date</c>; un registro de salario o de cargo programado después de esa fecha es
/// <c>hr.contract.records_after_end</c>.
/// </summary>
public sealed class DeactivateEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    EmployeeDetailComposer composer,
    IValidator<DeactivateRequest> validator)
    : ICommandHandler<DeactivateEmployeeCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(DeactivateEmployeeCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var employee = await employees.FindAsync(command.TenantId, new EmployeeId(command.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(command.EmployeeId);
        var contract = employee.ActiveContract
            ?? throw new HrDomainException(HrErrorCodes.EmployeeNotActive, "The employee has no active contract.");
        var date = command.Request.Date!.Value;
        if (date < contract.StartDate)
        {
            HrFieldErrors.Throw("date", "La fecha no puede ser anterior al inicio del contrato.");
        }

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        HrConflicts.Run(() => employee.Deactivate(date, calendar.UtcNow));

        await activity.AppendAsync(
            command.TenantId, employee, HrActivityAction.EmployeeDeactivated, HrActivityCategory.Lifecycle, contract.Id.Value,
            ActivityChanges.Deactivated(date), cancellationToken);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.EmployeeDeactivated, HrAuditActions.Resources.Employee,
            employee.Id.Value, ["status", "date"], calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), calendar.Today, cancellationToken);
    }
}
