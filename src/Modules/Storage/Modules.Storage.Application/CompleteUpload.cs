using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record CompleteUploadCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<FileResourceDto>;

/// <summary>
/// Completar una subida desde la consola: autoriza al miembro (<c>storage.file.upload</c> en el tenant de la ruta), carga el archivo
/// y pasa el guard de módulo; el resto es <see cref="UploadCompletion"/>, el mismo núcleo que usa la subida sin miembro de Soporte
/// (plan F31). La autorización vive sólo acá: ningún camino anónimo la comparte ni la afloja.
/// </summary>
public sealed class CompleteUploadHandler(
    IFileResourceRepository repository,
    ITenantModules tenantModules,
    IObjectStorage objectStorage,
    IFileContentInspector contentInspector,
    IImageVariantGenerator imageVariantGenerator,
    IPaymentProofImageProcessor paymentProofImageProcessor,
    IChatbotMediaProcessing chatbotMediaProcessing,
    IFileScanner scanner,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CompleteUploadCommand, FileResourceDto>
{
    private readonly UploadCompletion _completion = new(
        objectStorage,
        contentInspector,
        imageVariantGenerator,
        paymentProofImageProcessor,
        chatbotMediaProcessing,
        scanner,
        unitOfWork,
        auditPublisher,
        clock);

    public async Task<FileResourceDto> HandleAsync(
        CompleteUploadCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileUpload);

        var resource = await LoadAsync(command.TenantId, command.FileResourceId, cancellationToken);
        // Spec 2026-10-07: después del 404 —que no confirma que el id existe en otro tenant— y antes
        // de cualquier otra regla o efecto.
        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken);
        FileOwnerPermissionGuard.EnsureCallerMayAccess(executionContext, resource);

        await _completion.CompleteAsync(resource, executionContext.SubjectId, cancellationToken);
        return resource.ToDto();
    }

    private async Task<FileResource> LoadAsync(
        Guid tenantId, Guid fileResourceId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(
            new FileResourceId(fileResourceId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");

        if (resource.TenantId != tenantId)
        {
            // No filtrar la existencia entre tenants.
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Un adjunto de Soporte (dueño de sistema) lo completa sólo la subida sin miembro.
        SystemOwnedFileGuard.EnsureVisibleToMembers(resource);
        return resource;
    }
}
