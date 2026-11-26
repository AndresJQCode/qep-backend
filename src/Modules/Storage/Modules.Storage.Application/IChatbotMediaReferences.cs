namespace Modules.Storage.Application;

/// <summary>
/// Spec 2026-11-18 («Lifecycle») y regla 2 del owner: qué archivos de chatbot (<c>FileOwnerType.ChatbotMedia</c>) siguen en uso. Un archivo
/// está en uso mientras lo nombre un borrador, una versión publicada o una plantilla guardada del tenant, o un mensaje de la bandeja que un
/// bot mandó con él. Storage no conoce a Automations ni a Messaging: lo implementa un adaptador en Bootstrapper. Mismo contrato que
/// <see cref="ITemplateImageReferences"/>: la respuesta es el subconjunto de <paramref name="fileIds"/> que <paramref name="tenantId"/> usa;
/// un id que use otro tenant no cuenta.
/// </summary>
public interface IChatbotMediaReferences
{
    Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
