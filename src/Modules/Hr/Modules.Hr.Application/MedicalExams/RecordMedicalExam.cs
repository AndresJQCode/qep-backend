using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

public sealed record RecordMedicalExamCommand(Guid TenantId, Guid EmployeeId, MedicalExamRequest Request)
    : ICommand<EmployeeMedicalExamsDto>;

/// <summary>
/// Records an exam (spec 2026-11-23 §7). The same type and date twice is <c>hr.medical_exam.duplicate</c>; a future date is
/// <c>hr.medical_exam.future_date</c>. An exit exam may close the pending exit requirement, in the same unit of work.
/// </summary>
public sealed class RecordMedicalExamHandler(MedicalExamWrites writes, IValidator<MedicalExamRequest> validator)
    : ICommandHandler<RecordMedicalExamCommand, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(RecordMedicalExamCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeWriteAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var context = await writes.LoadAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var type = Enum.Parse<MedicalExamType>(request.Type!);
        MedicalExamWrites.EnsureNotDuplicated(context, type, request.ExamDate!.Value, except: null);

        var exam = MedicalExam.Create(
            MedicalExamId.New(),
            command.TenantId,
            context.Employee.Id,
            type,
            request.ExamDate.Value,
            context.Calendar.Today,
            request.Provider,
            request.Restrictions,
            request.Recommendations,
            request.RestrictionsUntil,
            context.Calendar.UtcNow);
        writes.Add(context, exam);

        return await writes.CommitAsync(
            command.TenantId,
            context,
            HrActivityAction.MedicalExamRecorded,
            ActivityChanges.MedicalExamRecorded(exam.Type, exam.ExamDate, hasDocument: false),
            HrAuditActions.MedicalExamCreated,
            HrAuditActions.Resources.MedicalExam,
            exam.Id.Value,
            cancellationToken);
    }
}
