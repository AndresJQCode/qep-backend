using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

public sealed record OpenMedicalExamUploadSessionCommand(Guid TenantId, string FileName, string MimeType, long SizeBytes)
    : ICommand<HrUploadSessionDto>;

/// <summary>
/// Opens the upload session of the PDF of a certificate (spec 2026-11-23 §7). Same shape and same limits as the contract PDFs
/// (<see cref="HrDocumentPolicy"/>), but on its own route and permission: <c>/files/upload-session</c> asks for contract permissions
/// that whoever manages exams does not have.
/// </summary>
public sealed class OpenMedicalExamUploadSessionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IHrDocumentStore store)
    : ICommandHandler<OpenMedicalExamUploadSessionCommand, HrUploadSessionDto>
{
    public async Task<HrUploadSessionDto> HandleAsync(OpenMedicalExamUploadSessionCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.MedicalRead,
            HrPermissions.MedicalManage);

        if (!HrDocumentPolicy.IsValidDeclaration(command.FileName, command.MimeType, command.SizeBytes))
        {
            throw new HrDomainException(HrErrorCodes.FileInvalid, "The file must be a PDF of up to 10 MB.");
        }

        var session = await store.OpenUploadAsync(
            command.TenantId, command.FileName.Trim(), HrDocumentPolicy.MimeType, command.SizeBytes, cancellationToken);
        return new HrUploadSessionDto(session.FileId, session.UploadUrl.AbsoluteUri, session.Headers);
    }
}
