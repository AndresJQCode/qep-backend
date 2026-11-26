using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record RewriteSalaryCommand(
    Guid TenantId, Guid EmployeeId, Guid ContractId, Guid SalaryId, SalaryRewriteRequest Request, string? IfMatch)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Reescribe un registro de salario en su lugar: conserva la fecha y cambia el monto. Es la única edición de un historial, por eso
/// exige la confirmación y el <c>If-Match</c>. El piso legal se evalúa a la <b>fecha del registro</b>, no a hoy.
/// </summary>
public sealed class RewriteSalaryHandler(
    ContractWrites writes,
    SalaryRules salaryRules,
    IValidator<SalaryRewriteRequest> validator)
    : ICommandHandler<RewriteSalaryCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(RewriteSalaryCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        HrSettingsSupport.EnsureVersion(employee.Version, expectedVersion);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        var record = ContractRules.FindSalary(contract, command.SalaryId);
        ContractRules.EnsureActive(contract);

        var floor = await salaryRules.FloorAtAsync(record.EffectiveDate, cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var (beforeAmount, beforeTeleworker) = (record.Amount, record.Teleworker);

        ContractRules.Run(() => employee.RewriteSalary(
            contract.Id,
            record.Id,
            request.Amount!.Value,
            request.Teleworker ?? false,
            request.AcknowledgeBelowMinimum ?? false,
            request.ConfirmRewrite == true,
            floor,
            calendar.UtcNow));

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.SalaryRewritten,
            HrActivityCategory.Salary,
            contract.Id.Value,
            ActivityChanges.SalaryRewritten(beforeAmount, beforeTeleworker, record),
            HrAuditActions.ContractSalaryRewritten,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
