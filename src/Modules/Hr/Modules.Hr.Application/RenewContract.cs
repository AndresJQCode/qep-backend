using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record RenewContractCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, RenewalRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Prorroga un contrato a término fijo. La Ley 2466/2025 bloquea, no avisa: pasar de los cuatro años es
/// <c>hr.contract.fixed_term_cap_exceeded</c> y, tras tres prórrogas cortas, una de menos de un año es
/// <c>hr.contract.renewal_minimum_one_year</c>. Una fecha que no pasa del fin vigente es 422 sobre <c>newEndDate</c>.
/// </summary>
public sealed class RenewContractHandler(
    ContractWrites writes,
    IHrDocumentStore documents,
    IValidator<RenewalRequest> validator)
    : ICommandHandler<RenewContractCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(RenewContractCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        ContractRules.EnsureActive(contract);
        var newEndDate = request.NewEndDate!.Value;
        ContractRules.EnsureNewEndDateAfterCurrent(contract, newEndDate);

        var document = await HrDocumentAttachment.ResolveAsync(documents, command.TenantId, request.DocumentFileId, cancellationToken);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var renewal = ContractRules.Run(() => employee.Renew(
            contract.Id, ContractRenewalId.New(), newEndDate, document, calendar.UtcNow));

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.ContractRenewed,
            HrActivityCategory.Contract,
            contract.Id.Value,
            ActivityChanges.ContractRenewed(renewal.PreviousEndDate, renewal.NewEndDate),
            HrAuditActions.ContractRenewed,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
