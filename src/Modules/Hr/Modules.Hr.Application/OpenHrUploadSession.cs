using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>Todo anulable: un campo ausente es un archivo inválido, no un 400 de binding.</summary>
public sealed record HrUploadSessionRequest(string? FileName, string? MimeType, long? SizeBytes);

public sealed record HrUploadSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);

public sealed record OpenHrUploadSessionCommand(Guid TenantId, string FileName, string MimeType, long SizeBytes)
    : ICommand<HrUploadSessionDto>;

/// <summary>
/// Abre la sesión de subida de un PDF. Un nombre en blanco o sin <c>.pdf</c>, otro tipo o un tamaño fuera de 1 byte a 10 MB se
/// rechaza acá con <c>hr.file.invalid</c>, sin llamar a Storage.
/// </summary>
public sealed class OpenHrUploadSessionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IHrDocumentStore store)
    : ICommandHandler<OpenHrUploadSessionCommand, HrUploadSessionDto>
{
    public async Task<HrUploadSessionDto> HandleAsync(OpenHrUploadSessionCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.ContractRead, HrPermissions.ContractManage);

        if (!HrDocumentPolicy.IsValidDeclaration(command.FileName, command.MimeType, command.SizeBytes))
        {
            throw new HrDomainException(HrErrorCodes.FileInvalid, "The file must be a PDF of up to 10 MB.");
        }

        var session = await store.OpenUploadAsync(
            command.TenantId, command.FileName.Trim(), HrDocumentPolicy.MimeType, command.SizeBytes, cancellationToken);
        return new HrUploadSessionDto(session.FileId, session.UploadUrl.AbsoluteUri, session.Headers);
    }
}
