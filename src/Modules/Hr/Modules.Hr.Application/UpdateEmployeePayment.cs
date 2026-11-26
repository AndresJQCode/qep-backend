using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateEmployeePaymentCommand(Guid TenantId, Guid EmployeeId, PaymentRequest Request, string? IfMatch)
    : ICommand<EmployeeDetailDto>;

/// <summary>El pago es dato de compensación: se lee y se escribe con <c>hr.contract.*</c>, no con los permisos de la ficha.</summary>
public sealed class UpdateEmployeePaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    EmployeeDetailComposer composer,
    IValidator<PaymentRequest> validator)
    : ICommandHandler<UpdateEmployeePaymentCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(UpdateEmployeePaymentCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.ContractRead,
            HrPermissions.ContractManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var employee = await employees.FindAsync(command.TenantId, new EmployeeId(command.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(command.EmployeeId);
        HrSettingsSupport.EnsureVersion(employee.Version, expectedVersion);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var before = employee.Payment;
        var after = EmployeeRules.ToPayment(command.Request);
        HrConflicts.Run(() => employee.SetPayment(after, calendar.UtcNow));

        var changes = ActivityChanges.Diff(before, after);
        if (changes.Count > 0)
        {
            await activity.AppendAsync(
                command.TenantId, employee, HrActivityAction.PaymentUpdated, HrActivityCategory.Payment, null, changes, cancellationToken);
        }

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.EmployeePaymentUpdated, HrAuditActions.Resources.Employee,
            employee.Id.Value, changes.Select(change => change.Field).ToList(), calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), calendar.Today, cancellationToken);
    }
}
