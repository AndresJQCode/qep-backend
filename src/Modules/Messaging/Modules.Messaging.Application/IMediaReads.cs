namespace Modules.Messaging.Application;

/// <summary>§8.6, «Servir»: el medio ya copiado de un mensaje del tenant. <c>MimeType</c> y <c>FileName</c>
/// salen de la fila, no del almacén: son los que decide la disposición del archivo.</summary>
public sealed record StoredMedia(string StorageKey, string MimeType, string? FileName, long? SizeBytes);

/// <summary>§8.6: <c>null</c> si el mensaje no es del tenant, no tiene medio o todavía no se copió.</summary>
public interface IMediaReads
{
    Task<StoredMedia?> FindStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
}
