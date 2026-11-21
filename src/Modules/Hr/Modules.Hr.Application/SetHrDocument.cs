using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>A qué se adjunta el PDF: el contrato o uno de sus tres registros.</summary>
public enum HrDocumentTarget
{
    Contract,
    Salary,
    Assignment,
    Renewal,
}

/// <param name="RecordId">El salario, el cargo y lugar o la prórroga; nulo cuando el destino es el contrato.</param>
/// <param name="FileId">El PDF ya subido; <c>null</c> lo quita.</param>
public sealed record SetHrDocumentCommand(
    Guid TenantId, Guid EmployeeId, Guid ContractId, HrDocumentTarget Target, Guid? RecordId, Guid? FileId)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Adjunta (o quita) el PDF de un contrato o de un registro. Se puede en cualquier estado del contrato: el PDF firmado puede llegar
/// después. Un archivo inexistente, ajeno o rechazado es 422 y no escribe nada.
/// </summary>
public sealed class SetHrDocumentHandler(ContractWrites writes, IHrDocumentStore documents)
    : ICommandHandler<SetHrDocumentCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(SetHrDocumentCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);

        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        var (target, before) = command.Target switch
        {
            HrDocumentTarget.Contract => ("contract", contract.DocumentFileName),
            HrDocumentTarget.Salary => ("salary", ContractRules.FindSalary(contract, RecordOf(command)).DocumentFileName),
            HrDocumentTarget.Assignment => ("assignment", ContractRules.FindAssignment(contract, RecordOf(command)).DocumentFileName),
            _ => ("renewal", ContractRules.FindRenewal(contract, RecordOf(command)).DocumentFileName),
        };

        var document = await HrDocumentAttachment.ResolveAsync(documents, command.TenantId, command.FileId, cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        ContractRules.Run(() =>
        {
            switch (command.Target)
            {
                case HrDocumentTarget.Contract:
                    employee.SetContractDocument(contract.Id, document, calendar.UtcNow);
                    break;
                case HrDocumentTarget.Salary:
                    employee.SetSalaryDocument(contract.Id, new SalaryRecordId(command.RecordId!.Value), document, calendar.UtcNow);
                    break;
                case HrDocumentTarget.Assignment:
                    employee.SetAssignmentDocument(contract.Id, new AssignmentRecordId(command.RecordId!.Value), document, calendar.UtcNow);
                    break;
                default:
                    employee.SetRenewalDocument(contract.Id, new ContractRenewalId(command.RecordId!.Value), document, calendar.UtcNow);
                    break;
            }
        });

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.DocumentAttached,
            HrActivityCategory.Contract,
            contract.Id.Value,
            ActivityChanges.DocumentAttached(target, before, document?.FileName),
            HrAuditActions.ContractDocumentUpdated,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }

    private static Guid RecordOf(SetHrDocumentCommand command) =>
        command.RecordId ?? throw new InvalidOperationException($"A {command.Target} document needs the record id.");
}
