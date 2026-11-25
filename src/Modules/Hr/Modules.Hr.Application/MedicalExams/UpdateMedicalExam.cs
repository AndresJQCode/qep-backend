using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

/// <param name="IfMatch">The version of the exam the screen loaded; it arrives raw and is read once authorized.</param>
public sealed record UpdateMedicalExamCommand(
    Guid TenantId, Guid EmployeeId, Guid ExamId, MedicalExamRequest Request, string? IfMatch)
    : ICommand<EmployeeMedicalExamsDto>;

/// <summary>
/// Replaces an exam. It may change the type and the date, so the exit requirement is recalculated: an exit exam edited into another
/// type, or dated before the termination, reopens it. The version is the exam's own, not the employee's.
/// </summary>
public sealed class UpdateMedicalExamHandler(MedicalExamWrites writes, IValidator<MedicalExamRequest> validator)
    : ICommandHandler<UpdateMedicalExamCommand, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(UpdateMedicalExamCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeWriteAsync(command.TenantId, cancellationToken);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var context = await writes.LoadAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var exam = MedicalExamWrites.Find(context, command.ExamId);
        HrSettingsSupport.EnsureVersion(exam.Version, expectedVersion);

        var type = Enum.Parse<MedicalExamType>(request.Type!);
        MedicalExamWrites.EnsureNotDuplicated(context, type, request.ExamDate!.Value, except: exam.Id);
        var before = (exam.Type, exam.ExamDate, exam.DocumentFileId is not null);
        exam.Update(
            type,
            request.ExamDate.Value,
            context.Calendar.Today,
            request.Provider,
            request.Restrictions,
            request.Recommendations,
            request.RestrictionsUntil,
            context.Calendar.UtcNow);

        return await writes.CommitAsync(
            command.TenantId,
            context,
            HrActivityAction.MedicalExamUpdated,
            ActivityChanges.MedicalExamUpdated(before, (exam.Type, exam.ExamDate, exam.DocumentFileId is not null)),
            HrAuditActions.MedicalExamUpdated,
            HrAuditActions.Resources.MedicalExam,
            exam.Id.Value,
            cancellationToken);
    }
}
