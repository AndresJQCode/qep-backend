using Modules.Storage.Domain;

namespace Modules.Storage.Application;

public interface IFileResourceRepository
{
    void Add(FileResource resource);

    Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken);

    // La versión en lote de GetAsync, con su misma semántica: sin filtro de tenant ni de estado,
    // y con las variantes cargadas. Quien la llama decide qué hacer con lo que llega —hoy
    // ProductImageLookup, que deja la regla de tenant a ProductImageResolver—. Un id que no
    // existe simplemente no aparece en el resultado.
    Task<IReadOnlyList<FileResource>> ListByIdsAsync(
        IReadOnlyCollection<FileResourceId> ids,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        // CAT-09: a qué entidad pertenecen los archivos que se piden. null = sin filtrar por
        // dueño, que es el comportamiento que este método tuvo hasta ahora.
        FileOwnerFilter? owner,
        // Spec 2026-10-07: los tipos de dueño cuyo módulo está apagado. Vacío = sin excluir.
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
