using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

public sealed record MarkExitNotAttendedCommand(Guid TenantId, Guid EmployeeId, NotAttendedRequest Request)
    : ICommand<EmployeeMedicalExamsDto>;

/// <summary>
/// The notice the employer owes the worker (spec 2026-11-23 §5.3): the oldest <c>Pending</c> exit requirement becomes
/// <c>NotAttended</c> with the date of the notice. Without a pending one it is <c>hr.medical_exam.no_pending_exit</c>. The notice
/// survives every later recalculation.
/// </summary>
public sealed class MarkExitNotAttendedHandler(MedicalExamWrites writes, IValidator<NotAttendedRequest> validator)
    : ICommandHandler<MarkExitNotAttendedCommand, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(MarkExitNotAttendedCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeWriteAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var context = await writes.LoadAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var pending = context.Requirements
            .Where(requirement => requirement.Status == ExitExamStatus.Pending)
            .OrderBy(requirement => requirement.TerminatedOn)
            .ThenBy(requirement => requirement.CreatedAt)
            .FirstOrDefault()
            ?? throw new HrDomainException(HrErrorCodes.MedicalExamNoPendingExit, "The employee has no pending exit exam.");
        pending.MarkNotAttended(command.Request.NotifiedOn!.Value, command.Request.Notes, context.Calendar.Today);

        return await writes.CommitAsync(
            command.TenantId,
            context,
            HrActivityAction.ExitExamNotAttended,
            ActivityChanges.ExitExamNotAttended(command.Request.NotifiedOn.Value),
            HrAuditActions.MedicalExamExitNotAttended,
            HrAuditActions.Resources.Employee,
            context.Employee.Id.Value,
            cancellationToken);
    }
}
