using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18: el archivo de un bloque «Mensaje» de chatbot, sobre Storage. Es de dueño de sistema
/// <see cref="FileOwnerType.ChatbotMedia"/> con el tenant como dueño: sube por <see cref="IStorageSystemUploads"/> (staging, sniffer de
/// Mensajería, antivirus, compresión de una imagen grande) y no aparece en la consola de Storage. Se publica al completar con
/// <see cref="FilePublication"/>, sin pasar por <c>PublishFileHandler</c> ni por sus permisos: lo autoriza
/// <c>automations.chatbot.manage</c>, como la imagen de encabezado de una plantilla (<see cref="MessagingTemplateImageStore"/>). Toda
/// lectura filtra por tenant y dueño: un id ajeno es el mismo «no existe».
/// </summary>
internal sealed class ChatbotMediaStore(
    IStorageSystemUploads uploads,
    IFileResourceRepository repository,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IStorageUnitOfWork storageUnitOfWork,
    IExecutionContext executionContext,
    IClock clock) : IChatbotMediaStore
{
    private const FileOwnerType OwnerType = FileOwnerType.ChatbotMedia;

    public async Task<ChatbotMediaSession> OpenUploadAsync(
        Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken)
    {
        SystemUploadSession session;
        try
        {
            session = await uploads.OpenAsync(tenantId, OwnerType, tenantId, fileName, contentType, sizeBytes, maxBytes, cancellationToken);
        }
        catch (StorageDomainException exception) when (TranslateDeclarationRejection(exception) is { } translated)
        {
            throw translated;
        }

        return new ChatbotMediaSession(session.FileId, session.UploadUrl, session.Headers);
    }

    // La política del chatbot ya revisó tipo y tope antes de llegar acá; si Storage igual rechaza la declaración (su tabla y la de
    // Mensajería se separaron), el editor ve el código de Automations sobre su campo, no uno de Storage que no conoce. Un nombre inválido
    // no se traduce: sigue siendo storage.file.name_*.
    private static AutomationsDomainException? TranslateDeclarationRejection(StorageDomainException exception) =>
        exception.Code switch
        {
            "storage.file.type_not_allowed" or "storage.file.mime_mismatch" => new AutomationsDomainException(
                AutomationsErrorCodes.MediaUnsupportedType,
                "This file type cannot be sent by a chatbot.",
                FieldError("fileName", ChatbotMediaMessages.UnsupportedType)),
            "storage.file.size_invalid" => new AutomationsDomainException(
                AutomationsErrorCodes.MediaTooLarge,
                "The file exceeds the maximum size for its kind.",
                FieldError("sizeBytes", ChatbotMediaMessages.TooLarge)),
            _ => null,
        };

    public async Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var result = await uploads.CompleteAsync(tenantId, fileId, OwnerType, tenantId, cancellationToken);
        switch (result.Outcome)
        {
            case SystemUploadOutcome.Available:
                break;
            case SystemUploadOutcome.ObjectMissing:
                return new ChatbotMediaCompletion(ChatbotMediaState.NotUploaded, null);
            case SystemUploadOutcome.Rejected:
                return new ChatbotMediaCompletion(ChatbotMediaState.Rejected, null);
            default:
                return new ChatbotMediaCompletion(ChatbotMediaState.NotFound, null);
        }

        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || !IsChatbotMediaOf(resource, tenantId))
        {
            return new ChatbotMediaCompletion(ChatbotMediaState.NotFound, null);
        }

        // Completar es idempotente (un reintento tras un corte): uno ya publicado responde su URL sin copiar ni auditar otra vez.
        if (resource.PublicStorageKey is null)
        {
            await filePublication.PublishAsync(resource, cancellationToken);
            auditPublisher.Publish(
                tenantId, executionContext.SubjectId, "storage.file.published", resource.Id.ToString(), "success", clock.UtcNow);
            await storageUnitOfWork.SaveChangesAsync(cancellationToken);
        }

        return FileOf(resource) is { } file
            ? new ChatbotMediaCompletion(ChatbotMediaState.Available, file)
            : new ChatbotMediaCompletion(ChatbotMediaState.Rejected, null);
    }

    public async Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return [];
        }

        var resources = await repository.ListByIdsAsync(
            fileIds.Distinct().Select(fileId => new FileResourceId(fileId)).ToList(), cancellationToken);
        return resources
            .Where(resource => IsChatbotMediaOf(resource, tenantId)
                && resource.Status is FileResourceStatus.Available
                && resource.PublicStorageKey is not null)
            .Select(FileOf)
            .OfType<ChatbotMediaFile>()
            .ToList();
    }

    private static bool IsChatbotMediaOf(FileResource resource, Guid tenantId) =>
        resource.TenantId == tenantId && resource.OwnerType == OwnerType && resource.OwnerId == tenantId;

    // Lo guardado, no lo declarado: nombre, tipo y tamaño son los que Storage verificó (y, tras comprimir, los del JPEG). Un formato que
    // Mensajería dejó de aceptar no se ofrece: el bot no manda lo que la política rechaza.
    private ChatbotMediaFile? FileOf(FileResource resource) =>
        ChatbotMediaPolicy.KindOfFileName(resource.Name) is { } kind && resource.PublicStorageKey is { } publicKey
            ? new ChatbotMediaFile(
                resource.Id.Value, kind, resource.Name, resource.MimeType, resource.SizeBytes, new Uri(publicStorage.GetUrl(publicKey)))
            : null;

    private static Dictionary<string, string[]> FieldError(string field, string message) =>
        new(StringComparer.Ordinal) { [field] = [message] };
}
