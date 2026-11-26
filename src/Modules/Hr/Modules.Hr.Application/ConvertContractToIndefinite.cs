using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record ConvertContractToIndefiniteCommand(Guid TenantId, Guid EmployeeId, Guid ContractId) : ICommand<EmployeeDetailDto>;

/// <summary>
/// Pasa un término fijo a indefinido: el contrato sigue, sin fin, y guarda el día del tenant en que ocurrió. La fecha de inicio no
/// cambia (es la misma relación laboral): una conversión de <i>tipo</i> es otra cosa y crea un contrato nuevo.
/// </summary>
public sealed class ConvertContractToIndefiniteHandler(ContractWrites writes)
    : ICommandHandler<ConvertContractToIndefiniteCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(ConvertContractToIndefiniteCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);

        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(contract);
        var previousEnd = contract.EndDate;
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);

        ContractRules.Run(() => employee.ConvertToIndefinite(contract.Id, calendar.Today, calendar.UtcNow));

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.ContractConvertedToIndefinite,
            HrActivityCategory.Contract,
            contract.Id.Value,
            ActivityChanges.ConvertedToIndefinite(previousEnd ?? calendar.Today, calendar.Today),
            HrAuditActions.ContractConvertedToIndefinite,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
