using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Activa (<c>Activate</c>) o desactiva un recurrente. Sin <c>If-Match</c>: un cambio de estado idempotente (I-8).</summary>
public sealed record ToggleRecurringConceptCommand(Guid TenantId, Guid CompanyId, Guid EmployeeId, Guid RecurringId, bool Activate)
    : ICommand<IReadOnlyList<RecurringConceptDto>>;

public sealed class ToggleRecurringConceptHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    PayrollEmployees employees,
    IRecurringConceptRepository recurring,
    RecurringConceptComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<ToggleRecurringConceptCommand, IReadOnlyList<RecurringConceptDto>>
{
    public async Task<IReadOnlyList<RecurringConceptDto>> HandleAsync(
        ToggleRecurringConceptCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        await employees.RequireInCompanyAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);

        var found = await recurring.FindAsync(command.TenantId, new RecurringConceptId(command.RecurringId), cancellationToken);
        if (found is null || found.CompanyId != command.CompanyId || found.EmployeeId != command.EmployeeId)
        {
            throw PayrollNotFound.Recurring(command.RecurringId);
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        PayrollConflicts.Run(() =>
        {
            if (command.Activate)
            {
                found.Activate(now);
            }
            else
            {
                found.Deactivate(now);
            }
        });

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId,
            command.Activate ? PayrollAuditActions.RecurringActivated : PayrollAuditActions.RecurringDeactivated,
            PayrollAuditActions.Resources.Recurring, found.Id.ToString(), ["active"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ListAsync(command.TenantId, command.CompanyId, command.EmployeeId, cancellationToken);
    }
}
