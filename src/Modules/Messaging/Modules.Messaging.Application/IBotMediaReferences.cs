namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-18 («Lifecycle»), regla 2 del owner: la bandeja conserva sus archivos. Qué archivos de chatbot (los de Storage, dueño
/// <c>ChatbotMedia</c>) nombra todavía un mensaje que un bot mandó (<c>message_media.storage_file_id</c>): mientras una conversación lo
/// muestre, Storage no lo suelta. Se pregunta por tenant; la respuesta es el subconjunto de <paramref name="fileIds"/> que un mensaje de
/// <paramref name="tenantId"/> referencia. Lo usa el adaptador de Bootstrapper que le responde a Storage.
/// </summary>
public interface IBotMediaReferences
{
    Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
