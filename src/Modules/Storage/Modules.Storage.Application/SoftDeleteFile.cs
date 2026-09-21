using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record SoftDeleteFileCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<SoftDeleteResult>;

public sealed class SoftDeleteFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    FilePublication filePublication,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SoftDeleteFileCommand, SoftDeleteResult>
{
    public async Task<SoftDeleteResult> HandleAsync(
        SoftDeleteFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileDelete);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        await filePublication.UnpublishAsync(resource, cancellationToken);
        // Borrado lógico; el objeto se retiene hasta que pase la ventana de retención.
        resource.SoftDelete(clock.UtcNow);

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.deleted",
            resource.Id.ToString(),
            "success",
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SoftDeleteResult(true);
    }
}
