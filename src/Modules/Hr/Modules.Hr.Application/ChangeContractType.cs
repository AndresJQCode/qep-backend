using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record ChangeContractTypeCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, NewContractRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// La conversión de tipo (por ejemplo, de aprendiz de etapa lectiva a productiva): cierra el contrato vigente como <c>Converted</c> el
/// día anterior al inicio del nuevo y crea otro, enlazado. Conserva la empresa: cambiar de empresa es terminar y reincorporar.
/// </summary>
public sealed class ChangeContractTypeHandler(
    ContractWrites writes,
    NewContractFactory contractFactory,
    IValidator<NewContractRequest> validator)
    : ICommandHandler<ChangeContractTypeCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(ChangeContractTypeCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var current = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(current);

        // Antes del factory: así el error dice la regla («no cambia la empresa») y no «elige un centro de esta empresa». El
        // dominio repite la guarda.
        if (command.Request.Contract!.CompanyId != current.CompanyId)
        {
            throw new HrDomainException(
                HrErrorCodes.CompanyChangeNotAllowed,
                "Convertir no cambia la empresa: termina el contrato y reincorpora en la otra.");
        }

        // D-12: el mismo empleador, activo o no, así que no se exige empresa activa.
        var (next, floor) = await contractFactory.BuildAsync(
            command.TenantId, command.Request, string.Empty, requireActiveCompany: false, cancellationToken);
        var names = await writes.NamesAsync(command.TenantId, [current.CompanyId], cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);

        ContractRules.Run(() => employee.ChangeContractType(current.Id, next, floor, calendar.UtcNow));

        var created = employee.ActiveContract!;
        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.ContractTypeChanged,
            HrActivityCategory.Contract,
            created.Id.Value,
            ActivityChanges.ContractTypeChanged(current, created, names),
            HrAuditActions.ContractTypeChanged,
            HrAuditActions.Resources.Contract,
            created.Id.Value,
            calendar,
            cancellationToken);
    }
}
