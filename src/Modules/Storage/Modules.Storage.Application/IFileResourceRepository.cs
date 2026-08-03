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
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
