using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

public sealed record DeleteMedicalExamCommand(Guid TenantId, Guid EmployeeId, Guid ExamId, string? IfMatch)
    : ICommand<EmployeeMedicalExamsDto>;

/// <summary>
/// Deletes an exam and answers the employee's exams as they are left, so the screen repaints from the response. If it was the exit
/// exam that closed a requirement, the requirement reopens.
/// </summary>
public sealed class DeleteMedicalExamHandler(MedicalExamWrites writes)
    : ICommandHandler<DeleteMedicalExamCommand, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(DeleteMedicalExamCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeWriteAsync(command.TenantId, cancellationToken);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);

        var context = await writes.LoadAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var exam = MedicalExamWrites.Find(context, command.ExamId);
        HrSettingsSupport.EnsureVersion(exam.Version, expectedVersion);
        writes.Remove(context, exam);

        return await writes.CommitAsync(
            command.TenantId,
            context,
            HrActivityAction.MedicalExamDeleted,
            ActivityChanges.MedicalExamDeleted(exam.Type, exam.ExamDate, exam.DocumentFileId is not null),
            HrAuditActions.MedicalExamDeleted,
            HrAuditActions.Resources.MedicalExam,
            exam.Id.Value,
            cancellationToken);
    }
}
