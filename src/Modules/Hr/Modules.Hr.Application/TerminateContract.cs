using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record TerminateContractCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, TerminateRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Termina el contrato vigente: el empleado queda <c>Retired</c>. Una fecha anterior al inicio es 422 sobre <c>date</c>; un registro
/// de salario o de cargo programado después de esa fecha es <c>hr.contract.records_after_end</c>. Funciona aunque la empresa se haya
/// desactivado (D-12).
/// </summary>
public sealed class TerminateContractHandler(ContractWrites writes, IValidator<TerminateRequest> validator)
    : ICommandHandler<TerminateContractCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(TerminateContractCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(contract);
        var date = request.Date!.Value;
        ContractRules.EnsureDateNotBeforeStart(contract, date);

        var reason = Enum.Parse<TerminationReason>(request.Reason!);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        ContractRules.Run(() => employee.Terminate(contract.Id, reason, date, HrText.Clean(request.Notes), calendar.UtcNow));

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.ContractTerminated,
            HrActivityCategory.Lifecycle,
            contract.Id.Value,
            ActivityChanges.Terminated(reason, date),
            HrAuditActions.ContractTerminated,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
