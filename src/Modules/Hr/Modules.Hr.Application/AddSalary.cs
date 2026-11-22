using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record AddSalaryCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, SalaryRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Un salario nuevo: un registro más que rige desde <c>effectiveDate</c> (el anterior se queda). El piso legal se evalúa a esa fecha,
/// no a hoy. «Hoy» es el del tenant: un salario de mañana en la zona del tenant sigue programado aunque en UTC ya sea mañana.
/// </summary>
public sealed class AddSalaryHandler(
    ContractWrites writes,
    IHrDocumentStore documents,
    SalaryRules salaryRules,
    IValidator<SalaryRequest> validator)
    : ICommandHandler<AddSalaryCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(AddSalaryCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(contract);
        var effectiveDate = request.EffectiveDate!.Value;
        ContractRules.EnsureEffectiveDateWithinContract(contract, effectiveDate);

        var document = await HrDocumentAttachment.ResolveAsync(documents, command.TenantId, request.DocumentFileId, cancellationToken);
        var floor = await salaryRules.FloorAtAsync(effectiveDate, cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var current = EffectiveDated.CurrentOf(contract.Salaries, calendar.Today);

        var id = SalaryRecordId.New();
        ContractRules.Run(() => employee.AddSalary(
            contract.Id,
            id,
            new SalaryEntry(effectiveDate, request.Amount!.Value, request.Teleworker ?? false, request.AcknowledgeBelowMinimum ?? false),
            floor,
            document,
            calendar.UtcNow));

        var next = contract.Salaries.Single(record => record.Id == id);
        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.SalaryCreated,
            HrActivityCategory.Salary,
            contract.Id.Value,
            ActivityChanges.SalaryCreated(current, next),
            HrAuditActions.ContractSalaryCreated,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
