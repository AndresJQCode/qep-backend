using Modules.Automations.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 §5.3 y D-M8: el adjunto que el cliente le mandó al bot, ya copiado a R2. La fila la lee
/// <see cref="IMediaReads"/> (por tenant: un mensaje de otra empresa no aparece) y los bytes y la URL firmada salen de
/// <see cref="IMessagingMediaStore"/>, el mismo almacén con que la bandeja sirve los medios.</summary>
internal sealed class AutomationMedia(IMediaReads reads, IMessagingMediaStore store) : IAutomationMedia
{
    public async Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        await reads.FindStoredAsync(tenantId, messageId, cancellationToken) is { } stored
            ? new StoredAttachment(stored.StorageKey, stored.FileName, stored.MimeType, stored.SizeBytes)
            : null;

    public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        store.CreateDownloadUrlAsync(key, lifetime, cancellationToken);

    public async Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        await store.OpenReadAsync(key, cancellationToken) is { } opened ? (opened.Content, opened.Length) : null;
}
