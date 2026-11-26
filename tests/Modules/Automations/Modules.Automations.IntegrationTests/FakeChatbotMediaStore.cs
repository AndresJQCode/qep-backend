using System.Collections.Concurrent;
using Modules.Automations.Application;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-18: Storage en memoria para el archivo de un bloque «Mensaje». Guarda los archivos por tenant: un id de otro tenant es
/// <see cref="ChatbotMediaState.NotFound"/>, como en el adaptador real. Completar lo deja disponible y publicado en
/// <c>https://public.test/&lt;fileId&gt;/&lt;fileName&gt;</c>.
/// </summary>
public sealed class FakeChatbotMediaStore : IChatbotMediaStore
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid FileId), Entry> _files = new();

    public Task<ChatbotMediaSession> OpenUploadAsync(
        Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken)
    {
        var fileId = Guid.CreateVersion7();
        _files[(tenantId, fileId)] = new Entry(fileName, contentType, sizeBytes, Completed: false);
        return Task.FromResult(new ChatbotMediaSession(
            fileId,
            new Uri($"https://r2.test/staging/{fileId}?upload"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = contentType, ["If-None-Match"] = "*" }));
    }

    public Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue((tenantId, fileId), out var entry))
        {
            return Task.FromResult(new ChatbotMediaCompletion(ChatbotMediaState.NotFound, null));
        }

        _files[(tenantId, fileId)] = entry with { Completed = true };
        return Task.FromResult(new ChatbotMediaCompletion(ChatbotMediaState.Available, FileOf(fileId, entry)));
    }

    public Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChatbotMediaFile>>(fileIds
            .Where(fileId => _files.TryGetValue((tenantId, fileId), out var entry) && entry.Completed)
            .Select(fileId => FileOf(fileId, _files[(tenantId, fileId)]))
            .ToList());

    private static ChatbotMediaFile FileOf(Guid fileId, Entry entry) =>
        new(fileId, KindOf(entry.ContentType), entry.FileName, entry.ContentType, entry.SizeBytes,
            new Uri($"https://public.test/{fileId}/{entry.FileName}"));

    private static string KindOf(string contentType) => contentType.Split('/')[0] switch
    {
        "image" => "image",
        "video" => "video",
        "audio" => "audio",
        _ => "document",
    };

    private sealed record Entry(string FileName, string ContentType, long SizeBytes, bool Completed);
}
