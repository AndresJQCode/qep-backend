using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>The body of <c>PUT …/document</c>: <c>null</c> removes the PDF.</summary>
public sealed record MedicalExamDocumentRequest(Guid? FileId);

public sealed record SetMedicalExamDocumentCommand(Guid TenantId, Guid EmployeeId, Guid ExamId, Guid? FileId)
    : ICommand<EmployeeMedicalExamsDto>;

/// <summary>
/// Attaches (or removes) the certificate PDF of an exam. A file that does not exist, is not of this tenant or was rejected is 422
/// and writes nothing, like every other Hr document. There is no <c>If-Match</c> on this route (spec §7), but the exam's version
/// still goes up.
/// </summary>
public sealed class SetMedicalExamDocumentHandler(MedicalExamWrites writes, IHrDocumentStore documents)
    : ICommandHandler<SetMedicalExamDocumentCommand, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(SetMedicalExamDocumentCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeWriteAsync(command.TenantId, cancellationToken);

        var context = await writes.LoadAsync(command.TenantId, command.EmployeeId, cancellationToken);
        var exam = MedicalExamWrites.Find(context, command.ExamId);
        var document = await HrDocumentAttachment.ResolveAsync(documents, command.TenantId, command.FileId, cancellationToken);
        var hadDocument = exam.DocumentFileId is not null;
        exam.SetDocument(document?.FileId, context.Calendar.UtcNow);

        return await writes.CommitAsync(
            command.TenantId,
            context,
            HrActivityAction.MedicalExamUpdated,
            ActivityChanges.MedicalExamUpdated((exam.Type, exam.ExamDate, hadDocument), (exam.Type, exam.ExamDate, exam.DocumentFileId is not null)),
            HrAuditActions.MedicalExamDocumentUpdated,
            HrAuditActions.Resources.MedicalExam,
            exam.Id.Value,
            cancellationToken);
    }
}

public sealed record GetMedicalExamDocumentQuery(Guid TenantId, Guid EmployeeId, Guid ExamId) : IQuery<HrDownloadUrlDto>;

/// <summary>The signed URL (5 minutes) of the certificate. Without <c>hr.medical.read</c> it is 403 even for whoever reads contracts.</summary>
public sealed class GetMedicalExamDocumentHandler(MedicalExamWrites writes, IHrDocumentStore documents)
    : IQueryHandler<GetMedicalExamDocumentQuery, HrDownloadUrlDto>
{
    public async Task<HrDownloadUrlDto> HandleAsync(GetMedicalExamDocumentQuery query, CancellationToken cancellationToken)
    {
        await writes.AuthorizeReadAsync(query.TenantId, cancellationToken);

        var context = await writes.LoadAsync(query.TenantId, query.EmployeeId, cancellationToken);
        var exam = MedicalExamWrites.Find(context, query.ExamId);
        var fileId = exam.DocumentFileId ?? throw new ResourceNotFoundException(HrErrorCodes.FileNotFound, "The exam has no certificate.");
        var url = await documents.CreateDownloadUrlAsync(query.TenantId, fileId, cancellationToken)
            ?? throw HrNotFound.File(fileId);

        return new HrDownloadUrlDto(url.AbsoluteUri);
    }
}
