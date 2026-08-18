using Modules.Storage.Domain;

namespace Modules.Storage.Application;

public interface IFileResourceRepository
{
    void Add(FileResource resource);

    Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken);

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
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
