using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record DeleteEmployeeCommand(Guid TenantId, Guid EmployeeId) : ICommand<bool>;

/// <summary>
/// Sólo un alta recién hecha, sin ningún movimiento posterior, se elimina (D-9): borra el empleado, su contrato y su historial de
/// acciones. <c>audit.entries</c> conserva <c>hr.employee.deleted</c>.
/// </summary>
public sealed class DeleteEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher)
    : ICommandHandler<DeleteEmployeeCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteEmployeeCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);

        var employee = await employees.FindAsync(command.TenantId, new EmployeeId(command.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(command.EmployeeId);
        employee.EnsureDeletable();

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        employees.Remove(employee);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.EmployeeDeleted, HrAuditActions.Resources.Employee,
            employee.Id.Value, [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
