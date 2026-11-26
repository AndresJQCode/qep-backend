using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public sealed record TerminateContractCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, TerminateRequest Request)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Termina el contrato vigente: el empleado queda <c>Retired</c>. Una fecha anterior al inicio es 422 sobre <c>date</c>; un registro
/// de salario o de cargo programado después de esa fecha es <c>hr.contract.records_after_end</c>. Funciona aunque la empresa se haya
/// desactivado (D-12).
/// </summary>
public sealed class TerminateContractHandler(
    ContractWrites writes,
    IValidator<TerminateRequest> validator,
    IMedicalExamRepository medicalExams,
    IHrBusinessCalendar businessCalendar,
    IHrMemberDirectory members,
    IMedicalExamEventPublisher events)
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

        // The exit exam (spec 2026-11-23 §5.3) is born with the termination, in the same unit of work as the termination itself.
        // Deactivating a contract is not a retirement and creates nothing. An exit exam already recorded and dated on or after the
        // termination closes it right away, so the same recalculation as after every exam write runs here.
        var dueDate = await businessCalendar.AddBusinessDaysAsync(
            command.TenantId, date, MedicalExamRules.ExitExamBusinessDays, cancellationToken);
        var requirement = ExitExamRequirement.Create(
            ExitExamRequirementId.New(), command.TenantId, employee.Id, contract.Id, date, dueDate, calendar.UtcNow);
        medicalExams.Add(requirement);
        var exams = await medicalExams.ListExamsAsync(command.TenantId, employee.Id, cancellationToken);
        var requirements = await medicalExams.ListRequirementsAsync(command.TenantId, employee.Id, cancellationToken);
        MedicalExamRules.Recalculate([.. requirements, requirement], exams);

        // Spec 2026-11-23 §9.2: one `exit-required` message per manager, written before the commit and in the same transaction, and
        // only while there is still an exam to get: with the requirement already closed nobody is told. No managers: the termination
        // goes on and nothing is written. If the lookup fails the termination fails, like any other read of this handler: there is no
        // termination without its notice.
        if (requirement.Status == ExitExamStatus.Pending)
        {
            var managers = await members.ListWithPermissionAsync(command.TenantId, HrPermissions.MedicalManage, cancellationToken);
            foreach (var manager in managers)
            {
                events.AddExitRequired(
                    command.TenantId, manager.MemberId, employee.Id.Value, $"{employee.FirstName} {employee.LastName}", dueDate,
                    contract.Id.Value, calendar.UtcNow);
            }
        }
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
