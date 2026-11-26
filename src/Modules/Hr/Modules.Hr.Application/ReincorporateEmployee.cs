using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record ReincorporateEmployeeCommand(Guid TenantId, Guid EmployeeId, NewContractRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Un contrato nuevo para quien ya no tiene uno vigente. La empresa puede ser otra (es como se cambia de empresa), pero tiene que
/// estar activa: es un vínculo nuevo (D-12). Las claves de <c>errors</c> son planas (<c>contract.companyId</c>,
/// <c>assignment.workplaceId</c>).
/// </summary>
public sealed class ReincorporateEmployeeHandler(
    ContractWrites writes,
    NewContractFactory contractFactory,
    IValidator<NewContractRequest> validator)
    : ICommandHandler<ReincorporateEmployeeCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(ReincorporateEmployeeCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var (next, floor) = await contractFactory.BuildAsync(
            command.TenantId, command.Request, string.Empty, requireActiveCompany: true, cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);

        var previousStatus = employee.Status;
        ContractRules.Run(() => employee.Reincorporate(next, floor, calendar.UtcNow));

        var created = employee.ActiveContract!;
        var names = await writes.NamesAsync(command.TenantId, [created.CompanyId], cancellationToken);
        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.EmployeeReincorporated,
            HrActivityCategory.Lifecycle,
            created.Id.Value,
            ActivityChanges.Reincorporated(previousStatus, created, names),
            HrAuditActions.EmployeeReincorporated,
            HrAuditActions.Resources.Employee,
            employee.Id.Value,
            calendar,
            cancellationToken);
    }
}
