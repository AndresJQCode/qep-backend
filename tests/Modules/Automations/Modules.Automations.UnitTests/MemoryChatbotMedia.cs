using Modules.Automations.Application;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-18: los archivos de chatbot de un tenant en memoria. Sólo resuelve; <see cref="Asked"/> guarda cada lectura.</summary>
internal sealed class MemoryChatbotMedia(params ChatbotMediaFile[] files) : IChatbotMediaStore
{
    public List<(Guid TenantId, IReadOnlyCollection<Guid> FileIds)> Asked { get; } = [];

    public Task<ChatbotMediaSession> OpenUploadAsync(
        Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        Asked.Add((tenantId, [.. fileIds]));
        return Task.FromResult<IReadOnlyList<ChatbotMediaFile>>([.. files.Where(file => fileIds.Contains(file.FileId))]);
    }
}
