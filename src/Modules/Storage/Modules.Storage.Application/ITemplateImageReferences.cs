namespace Modules.Storage.Application;

/// <summary>
/// Spec 2026-11-17, «Closing the residual gaps»: qué imágenes de encabezado (<c>FileOwnerType.MessageTemplate</c>) sigue usando una
/// plantilla. Storage no conoce a Messaging: lo implementa un adaptador en Bootstrapper. Se pregunta por tenant, y la respuesta es el
/// subconjunto de <paramref name="fileIds"/> que una fila de plantilla de <paramref name="tenantId"/> referencia; un id que use otro tenant
/// no cuenta.
/// </summary>
public interface ITemplateImageReferences
{
    Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
