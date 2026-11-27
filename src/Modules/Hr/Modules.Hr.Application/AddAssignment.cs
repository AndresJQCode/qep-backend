using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record AddAssignmentCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, AssignmentRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Un cargo y lugar de trabajo nuevo desde <c>effectiveDate</c>. El cargo, el área y el centro siguen las mismas reglas que en un
/// contrato nuevo (claves sin prefijo) y el centro tiene que ser de una sede de la empresa del contrato. No revalida que la empresa
/// siga activa: es un contrato existente (D-12).
/// </summary>
public sealed class AddAssignmentHandler(
    ContractWrites writes,
    IHrSettingsRepository settings,
    IHrDocumentStore documents,
    IValidator<AssignmentRequest> validator)
    : ICommandHandler<AddAssignmentCommand, EmployeeDetailDto>
{
    private static readonly AssignmentKeys Keys = new("positionId", "areaId", "workplaceId");

    public async Task<EmployeeDetailDto> HandleAsync(AddAssignmentCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(contract);
        var effectiveDate = request.EffectiveDate!.Value;
        ContractRules.EnsureEffectiveDateWithinContract(contract, effectiveDate);

        var (position, area, workplace, site) = await AssignmentLookups.ResolveAsync(
            settings, command.TenantId, request.PositionId!.Value, request.AreaId, request.WorkplaceId!.Value, Keys, null, cancellationToken);
        ContractRules.EnsureWorkplaceOfCompany(site, contract.CompanyId, Keys.Workplace);
        var document = await HrDocumentAttachment.ResolveAsync(documents, command.TenantId, request.DocumentFileId, cancellationToken);

        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var names = await writes.NamesAsync(command.TenantId, [contract.CompanyId], cancellationToken);
        var previous = EffectiveDated.CurrentOf(contract.Assignments, calendar.Today);

        var id = AssignmentRecordId.New();
        ContractRules.Run(() => employee.AddAssignment(
            contract.Id, id, new AssignmentEntry(effectiveDate, position.Id, area?.Id, site.Id, workplace.Id), document, calendar.UtcNow));

        var next = contract.Assignments.Single(record => record.Id == id);
        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.AssignmentCreated,
            HrActivityCategory.Assignment,
            contract.Id.Value,
            ActivityChanges.AssignmentCreated(previous, next, names),
            HrAuditActions.ContractAssignmentCreated,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
