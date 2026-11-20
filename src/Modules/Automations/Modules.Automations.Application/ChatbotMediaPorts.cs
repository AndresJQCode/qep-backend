namespace Modules.Automations.Application;

// Spec 2026-11-18: lo que Automations necesita de Mensajería (qué archivos puede mandar el bot) y de Storage (subir, completar y resolver
// el archivo de un bloque «Mensaje»). Los dos los implementa el Bootstrapper: Automations no referencia ninguno de los dos módulos.

/// <summary>Lo que el bot puede mandar de una extensión: su tipo (<c>image</c>, <c>video</c>, <c>audio</c>, <c>document</c>), el MIME
/// canónico y el tope, de la columna WhatsApp de la política de Mensajería.</summary>
public sealed record ChatbotMediaRule(string Kind, string ContentType, long MaxBytes);

/// <summary>La política de Mensajería leída en vivo: el bot acepta lo mismo que un asesor manda por WhatsApp.</summary>
public interface IChatbotMediaPolicy
{
    /// <summary>La regla de la extensión del nombre; <c>null</c> = not sendable.</summary>
    ChatbotMediaRule? RuleFor(string fileName);
}

/// <summary>El archivo en Storage (dueño <c>ChatbotMedia</c>, del tenant), publicado al bucket público al completar.</summary>
public interface IChatbotMediaStore
{
    Task<ChatbotMediaSession> OpenUploadAsync(
        Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Completes (idempotent), publishes, and reports. Foreign/unknown id → NotFound.</summary>
    Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>One batch lookup: Available + published ChatbotMedia files of this tenant; absent ids are simply missing.</summary>
    Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}

/// <summary><see cref="Headers"/> son exactamente las cabeceras que firmó el PUT (<c>Content-Type</c>, <c>If-None-Match</c>).</summary>
public sealed record ChatbotMediaSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);

public enum ChatbotMediaState
{
    Available,
    NotFound,
    NotUploaded,
    Rejected,
}

/// <summary><see cref="File"/> va sólo con <see cref="ChatbotMediaState.Available"/>.</summary>
public sealed record ChatbotMediaCompletion(ChatbotMediaState State, ChatbotMediaFile? File);

/// <summary>Un archivo listo para mandar: <see cref="Kind"/> en minúscula y <see cref="Url"/> la URL pública absoluta.</summary>
public sealed record ChatbotMediaFile(Guid FileId, string Kind, string FileName, string ContentType, long SizeBytes, Uri Url);

/// <summary>Nombres de los eventos de integración que Automations escribe en <c>platform.outbox_messages</c> para otros módulos.</summary>
public static class AutomationsIntegrationEvents
{
    /// <summary>
    /// Spec 2026-11-18 («Lifecycle»): se borró un chatbot y sus bloques «Mensaje» nombraban archivos (los del borrador y los de cada versión).
    /// Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>. Lo consume Storage, que suelta los que ya nadie usa;
    /// Storage lo nombra igual en <c>StorageSubscribedEvents</c>.
    /// </summary>
    public const string ChatbotMediaReleased = "automations.chatbot.media-released.v1";
}

/// <summary>
/// Encola el aviso <see cref="AutomationsIntegrationEvents.ChatbotMediaReleased"/> para que Storage suelte los archivos de un chatbot
/// borrado. No guarda: el aviso viaja con el <c>SaveChangesAsync</c> de quien llama, en la misma transacción que el borrado, o no sale
/// nada. Copia de <c>ITemplateHeaderImageReleases</c> (Messaging), con varios archivos.
/// </summary>
public interface IChatbotMediaReleases
{
    void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt);
}
